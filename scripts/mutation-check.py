#!/usr/bin/env python3
"""Mutation table for the bug register (spec FR-5): re-introduce each defect and prove a named test kills it.

    python3 scripts/mutation-check.py [--only F-1,A-2] [--markdown]

For every row the script (1) requires the source file to be clean in git, (2) applies the textual mutation, (3) runs the
test project and counts failures, (4) restores the file with `git checkout`. A mutant is KILLED when the build still
succeeds and at least one test fails; the names of the failing tests are listed. A row whose mutant survives is printed
as SURVIVED (an equivalent mutant must be explained in the table, never hidden). Needs the same environment as the build
(TabTabGoCoreVersion when the local Core feed is used) and Docker for the emulator-backed rows (they only add kills).
"""
import argparse
import os
import pathlib
import time
import re
import subprocess
import sys

REPO = pathlib.Path(__file__).resolve().parent.parent

# Mutants that cannot change observable behaviour on this line (explained in their description).
EQUIVALENT = {"E-6", "M11"}

# id, severity, project, file, old, new, description
M = [
    ("F-1", "C", "FileStorage.Tests", "src/TabTabGo.Storage/Paths/StoragePath.cs",
     "            if (IsDotsOrSpaces(segment))", "            if (false && IsDotsOrSpaces(segment))", "path traversal: '..' segments accepted"),
    ("F-2", "C", "FileStorage.Tests", "src/TabTabGo.Storage.FileStorage/FileStorageProvider.cs",
     "            Directory.CreateDirectory(Path.GetDirectoryName(full)!);\n            target = new FileStream", "            target = new FileStream",
     "nested path: parent directory not created (no-overwrite path)"),
    ("A-1", "C", "Aws.Tests", "src/TabTabGo.Storage.Aws/S3StorageProvider.cs",
     "            memory.Position = 0;\n            return memory;", "            return memory;", "S3 retrieve stream left at the end"),
    ("A-2", "C", "Aws.Tests", "src/TabTabGo.Storage.Aws/S3StorageProvider.cs",
     '            throw Failure("Store", container, relative, e);', "            return relative;", "S3 store swallows upload errors (data loss)"),
    ("Z-1", "C", "Azure.Tests", "src/TabTabGo.Storage.AzureStorage/BlobStorageProvider.cs",
     "                return blob.GenerateSasUri(builder);", "                return new Uri(blob.GenerateSasUri(builder).AbsolutePath, UriKind.Relative);", "Azure SAS URL without host/query"),
    ("V-1", "C", "Services.Tests", "src/TabTabGo.Storage.Services/Internal/FileMetadata.cs",
     "        if (source.Title is not null)\n        {\n            target.Title = source.Title;", "        target.FilePath = source.FilePath ?? target.FilePath;\n        if (source.Title is not null)\n        {\n            target.Title = source.Title;",
     "update copies the client's FilePath"),
    ("V-2", "C", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "        catch\n        {\n            await CleanupAsync(stored).ConfigureAwait(false);\n            throw;\n        }\n\n        await PostCreate", "        catch\n        {\n            throw;\n        }\n\n        await PostCreate",
     "upload: stored objects are not removed when the insert fails"),
    ("H1", "C", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "        RestoreOwned(tracked, truth);\n        return tracked;", "        return tracked;", "mass assignment: tracked storage-owned fields are used as is"),
    ("SoftDelete", "C", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "        file.IsEnabled = false;\n        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);\n        await PostDelete(file, cancellationToken)",
     "        await HardDeleteAsync([file], cancellationToken).ConfigureAwait(false);\n        await PostDelete(file, cancellationToken)", "Delete(id) hard-deletes instead of soft-deleting"),
    ("X-3", "M", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "        EnsureValidToUpdate(file, _ => { }, 4001);\n\n        await PreUpdate(file, file, cancellationToken)", "        await PreUpdate(file, file, cancellationToken)", "Replace skips IsValidToUpdate"),
    ("X-4", "M", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "        EnsureAllDeletable(files);\n        await PreDelete(files, cancellationToken).ConfigureAwait(false);\n        foreach (var file in files)\n        {\n            file.IsEnabled = false;",
     "        await PreDelete(files, cancellationToken).ConfigureAwait(false);\n        foreach (var file in files)\n        {\n            file.IsEnabled = false;", "bulk Delete(ids) skips IsValidToDelete"),
    ("F-5", "M", "FileStorage.Tests", "src/TabTabGo.Storage.FileStorage/Services/PathService.cs",
     "            if (underWebRoot || underWwwroot)", "            if (false)", "no warning when the storage root is under wwwroot"),
    ("F-6", "M", "FileStorage.Tests", "src/TabTabGo.Storage.FileStorage/FileStorageProvider.cs",
     "            File.Move(temp, full, overwrite);\n            return relative;", "            File.Copy(temp, full, overwrite);\n            return relative;", "overwrite is not atomic (copy instead of move)"),
    ("A-3", "M", "Aws.Tests", "src/TabTabGo.Storage.Aws/S3StorageProvider.cs",
     "        catch (AmazonS3Exception e) when (IsNotFound(e))\n        {\n            return null;\n        }\n        catch (Exception e) when (IsUpstream(e))\n        {\n            throw Failure(\"Retrieve\"",
     "        catch (Exception e) when (IsUpstream(e))\n        {\n            return null;\n        }\n        catch (Exception e) when (IsUpstream(e))\n        {\n            throw Failure(\"Retrieve\"", "every S3 read error becomes 'not found'"),
    ("A-5", "M", "Aws.Tests", "src/TabTabGo.Storage.Aws/S3ClientFactory.cs",
     "        return string.IsNullOrEmpty(configuration.AccessKey) || string.IsNullOrEmpty(configuration.SecretKey)\n            ? null\n            : new BasicAWSCredentials(configuration.AccessKey, configuration.SecretKey);",
     "        return new BasicAWSCredentials(configuration.AccessKey ?? string.Empty, configuration.SecretKey ?? string.Empty);", "static keys mandatory: default credential chain not used"),
    ("A-13", "M", "Aws.Tests", "src/TabTabGo.Storage.Aws/S3StorageProvider.cs",
     '                IfNoneMatch = options is { Overwrite: false } ? "*" : null,', "                IfNoneMatch = null,", "overwrite:false is not conditional (race)"),
    ("Z-2", "M", "Azure.Tests", "src/TabTabGo.Storage.AzureStorage/BlobStorageProvider.cs",
     "            if (blob.CanGenerateSasUri)\n            {\n                return blob.GenerateSasUri(builder);",
     "            if (blob.CanGenerateSasUri)\n            {\n                Console.WriteLine(blob.GenerateSasUri(builder));\n                return blob.GenerateSasUri(builder);", "SAS URL printed to the console"),
    ("Z-7", "M", "Azure.Tests", "src/TabTabGo.Storage.AzureStorage/AzureBlobStorageOptions.cs",
     "        return ValidateOptionsResult.Fail(\n            \"Configure AzureBlobStorage:ConnectionString", "        return ValidateOptionsResult.Success;\n#pragma warning disable CS0162\n        return ValidateOptionsResult.Fail(\n            \"Configure AzureBlobStorage:ConnectionString",
     "options not validated at start"),
    ("Z-9", "M", "Azure.Tests", "src/TabTabGo.Storage.AzureStorage/ContainerNameNormalizer.cs",
     "        if (name.Contains(\"--\", StringComparison.Ordinal))", "        if (false)", "'--' runs not collapsed (Azure 400)"),
    ("V-5", "M", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "        if (_options.DeniedExtensions.Any(", "        if (false && _options.DeniedExtensions.Any(", "denied extensions accepted (stored XSS)"),
    ("V-12", "M", "Services.Tests", "src/TabTabGo.Storage.Services/ImageService.cs",
     "            if ((long)info.Width * info.Height > _options.MaxPixels)", "            if (false)", "decompression-bomb guard removed"),
    ("X-1", "M", "Tests", "src/TabTabGo.Storage/ContentTypes/ContentTypeMap.cs",
     '        ["pdf"] = "application/pdf",', '        ["pdf"] = "application/pdf",\n        ["xml"] = "application/xml",\n        ["html"] = "text/html",', "xml/html mapped to their native, script-capable types"),
    ("X-5", "M", "Tests", "src/TabTabGo.Storage/ContentTypes/ContentTypeMap.cs",
     "        IsSafeToRender(contentType) && !string.Equals(contentType, \"text/plain\"", "        IsSafeToRender(contentType) && !string.Equals(contentType, \"text/plain-x\"", "text/plain allowed inline on signed URLs"),
    ("Sym", "M", "FileStorage.Tests", "src/TabTabGo.Storage.FileStorage/FileStorageProvider.cs",
     "        if (!RealPath(full).StartsWith(realRoot, comparison))", "        if (false)", "symlink planted in a container escapes the root"),
    ("Excl", "M", "FileStorage.Tests", "src/TabTabGo.Storage.FileStorage/FileStorageProvider.cs",
     "FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);\n        }\n        catch (IOException) when (File.Exists(full))",
     "FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);\n        }\n        catch (IOException) when (File.Exists(full))", "no-overwrite store can overwrite (two winners)"),
    ("Cont", "M", "Azure.Tests", "src/TabTabGo.Storage/Providers/StorageProvider.cs",
     " || StoragePath.Normalize(container).Contains('/')", "", "nested container names accepted"),
    ("X-11/N1", "H", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "        IsValidToUpdate(current, out validationErrorMessage) && IsValidToUpdate(merged, out validationErrorMessage);",
     "        IsValidToUpdate(merged, out validationErrorMessage);", "update gate judges only the merged entity (record takeover)"),
    ("N2", "M", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "            property.SetValue(target, property.GetValue(truth));", "            _ = property;", "tracked host columns / UpdatedBy are not restored from the database"),
    ("N2x", "M", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "        target.ExtraProperties = truth.ExtraProperties is null\n            ? new Dictionary<string, object>()\n            : new Dictionary<string, object>(truth.ExtraProperties);\n    }",
     "    }", "tracked ExtraProperties are not restored"),
    ("N3", "M", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "            await PreDelete(file, ct).ConfigureAwait(false);", "            await Task.CompletedTask;", "bulk and purge deletes skip the single-entity PreDelete hook"),
    ("N3b", "M", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "            if (purge ? !IsValidToPurge(file, out error) : !IsValidToDelete(file, out error))", "            if (!IsValidToDelete(file, out error))", "purge ignores IsValidToPurge"),
    ("N4", "M", "Azure.Tests", "src/TabTabGo.Storage.AzureStorage/ContainerNameNormalizer.cs",
     "input.Trim().ToLowerInvariant()", "input.Trim().ToLower()", "culture-sensitive lower-casing (tr-TR: Image -> mage)"),
    ("N5", "M", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "        metadata.FileExtension = prepared.Extension;", "", "create gate sees the metadata's claimed extension"),
    ("N5b", "M", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "        if (!_options.AllowMetadataExtraProperties)", "        if (false)", "metadata ExtraProperties kept by default"),
    ("N6", "M", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "entity => entity.FileId == id && entity.IsEnabled;", "entity => entity.FileId == id;", "soft-deleted files show through GetByKey/Exists"),
    ("N7", "L", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "        metadata.IsCompressed = false;", "        metadata.IsCompressed = null;", "IsCompressed falls back to the column default (true)"),
    ("N8", "L", "Tests", "src/TabTabGo.Storage/ContentTypes/ContentDispositionFormatter.cs",
     " or '\\u2060' or '\\uFEFF' or '\\u2028' or '\\u2029'", "", "zero-width characters kept in names"),
    ("Tracked", "M", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "        entity = incomingCopy;", "", "caller-edited tracked instance loses its allow-listed edits"),
    ("M15", "M", "Aws.Tests", "src/TabTabGo.Storage.Aws/S3ResponseStream.cs",
     "            _owner.Dispose();", "", "S3 response (and its HTTP connection) is not disposed with the stream"),
    ("M11", "M", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "        if (file.FileType != FileType.Image)\n        {\n            throw NotFound(id);\n        }\n\n        var container", "        var container", "GetImage serves non-images (equivalent: the container is the file type, so a non-image has no object under Image and still gets 404; the check is defence in depth)"),
    ("R4-A1", "M", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "            FileMetadata<TFile>.CopyFrom(current, entity, _options.AllowMetadataExtraProperties);", "            FileMetadata<TFile>.CopyFrom(current, entity, true);", "update merges ExtraProperties although the option is off"),
    ("R4-A2", "M", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "            if (!_options.AllowMetadataExtraProperties &&\n                string.Equals(first, nameof(IFile.ExtraProperties)", "            if (false &&\n                string.Equals(first, nameof(IFile.ExtraProperties)", "patch on /ExtraProperties allowed although the option is off"),
    ("R4-A3", "M", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "        if (bag.Count > _options.MaxExtraPropertyKeys ||\n            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(bag).Length > _options.MaxExtraPropertiesBytes)", "        if (false)", "ExtraProperties key/size limits not enforced"),
    ("R4-C", "M", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "        EnsureNoPendingNavigationChange(tracked, truth);\n        return tracked;", "        return tracked;", "an edited navigation silently changes the foreign key"),
    ("R4-C2", "M", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "        _ = _unitOfWork.GetChanges();", "", "navigation edits are not detected before the comparison"),
    ("R4-D", "L", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "            current.UpdatedDate = _timeProvider.GetUtcNow();\n", "", "UpdatedDate not set on update"),
    ("R4-F", "L", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "        return file is not null && IsValidToDelete(file, out _);", "        return file is null || IsValidToDelete(file, out _);", "CanDelete says yes for a missing / soft-deleted file"),
    ("R4-G", "L", "Tests", "src/TabTabGo.Storage/ContentTypes/ContentDispositionFormatter.cs",
     "char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format ||", "", "Unicode format characters other than the listed ones survive"),
    ("R4-B1", "M", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "includeProperties, ActiveFilter.And(fixCriteria), flags);", "includeProperties, fixCriteria, flags);", "OData reads pass the caller's criteria without the active filter (soft-deleted rows returned)"),
    ("R4-B2", "M", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "base.GetList(ActiveFilter.And<TFile>(query!)!, includeProperties", "base.GetList(query!, includeProperties", "GetList returns soft-deleted rows"),
    ("R5-G1", "M", "Tests", "src/TabTabGo.Storage/ContentTypes/ContentDispositionFormatter.cs",
     "Rune.GetUnicodeCategory(rune) != System.Globalization.UnicodeCategory.Format", "true", "non-BMP format characters (tag characters, musical controls) survive"),
    ("R5-G2", "M", "Tests", "src/TabTabGo.Storage/ContentTypes/ContentDispositionFormatter.cs",
     "char.IsSurrogate(c) || c < ' '", "c < ' '", "lone surrogates survive in names"),
    ("R6-1", "L", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "            file.IsEnabled = true;\n            file.UpdatedDate = previousUpdated;\n            throw;", "            throw;",
     "a failed save of a soft delete leaves the new UpdatedDate / IsEnabled on the tracked entity"),
    ("R6-2", "L", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "            current.UpdatedDate = oldUpdated;\n", "", "a failed save of an update leaves the new UpdatedDate on the tracked entity"),
    ("R6-3", "L", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "            file.UpdatedDate = previousUpdated;\n            await CleanupAsync(stored)", "            await CleanupAsync(stored)",
     "a failed save of a replace leaves the new UpdatedDate on the tracked entity"),
    ("R6-4", "L", "Services.Tests", "src/TabTabGo.Storage.Services/FileService.cs",
     "        // Change detection scans every tracked entity: run it once for the batch, then compare each file.\n        _ = _unitOfWork.GetChanges();\n",
     "", "batch loads stop forcing change detection (navigation edits go undetected)"),
    ("E-6", "M", "Data.EF.Tests", "src/TabTabGo.Storage.Data.EF/StorageModelsBuilder.cs",
     "        modelBuilder.Property(f => f.CreatedBy).IsRequired();", "", "audit-column pin removed (equivalent on v8: Core 8 columns are already required; it only bites on Core 10)"),
]


def run(cmd):
    return subprocess.run(cmd, cwd=REPO, text=True, capture_output=True)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--only")
    ap.add_argument("--markdown", action="store_true")
    args = ap.parse_args()
    only = set(args.only.split(",")) if args.only else None
    rows = []
    for rid, sev, project, rel, old, new, what in M:
        if only and rid not in only:
            continue
        path = REPO / rel
        if run(["git", "diff", "--quiet", "--", rel]).returncode != 0:
            sys.exit(f"{rel} has uncommitted changes; commit or stash first")
        text = path.read_text()
        if old not in text:
            rows.append((rid, sev, what, "STALE", "mutation text not found - update the table"))
            continue
        path.write_text(text.replace(old, new, 1))
        time.sleep(1.1)   # a fresh mtime, so the incremental build cannot reuse the previous mutant's output
        os.utime(path)
        try:
            result = run(["dotnet", "test", f"tests/TabTabGo.Storage.{project}", "-c", "Release", "--no-restore"])
        finally:
            run(["git", "checkout", "--quiet", "--", rel])
            time.sleep(1.1)
            os.utime(path)
        out = result.stdout + result.stderr
        if re.search(r" error CS\d+", out):
            rows.append((rid, sev, what, "NO-BUILD", "mutant does not compile"))
            continue
        failed = sorted(set(re.findall(r"^\s+Failed (\S+)", out, re.M)))
        names = [f.split(".")[-1].split("(")[0] for f in failed]
        verdict = "KILLED" if failed else "SURVIVED"
        rows.append((rid, sev, what, verdict, f"{len(failed)} failing: " + ", ".join(sorted(set(names))[:4]) + (" ..." if len(set(names)) > 4 else "")))
        print(f"{rid:8} {verdict:9} {len(failed):3} {what}", flush=True)
    if args.markdown:
        print("\n| Bug | Sev | Mutation | Result | Killing tests |\n|---|---|---|---|---|")
        for rid, sev, what, verdict, detail in rows:
            print(f"| {rid} | {sev} | {what} | {verdict} | {detail} |")
    return 0 if all(r[3] == "KILLED" or r[0] in EQUIVALENT for r in rows) else 1


if __name__ == "__main__":
    sys.exit(main())
