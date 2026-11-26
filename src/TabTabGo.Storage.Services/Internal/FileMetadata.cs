using System.Linq.Expressions;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TabTabGo.Core.Exceptions;
using TabTabGo.Storage.Entities;
using TabTabGo.Storage.Enums;
using TabTabGo.Storage.Models;

namespace TabTabGo.Storage.Services.Internal;

/// <summary>
/// The client-updatable metadata of a file, applied by an explicit allow-list. <see cref="IFile"/> does not declare
/// <c>ReferenceType</c>, <c>ReferenceId</c> or <c>Duration</c>, so those are read by reflection when the entity has them.
/// Storage-owned fields (path, size, extension, original name and media type) are never touched here.
/// </summary>
internal static class FileMetadata<TFile>
    where TFile : class, IFile
{
    public static readonly string[] AllowedNames =
    [
        "Title", "Comment", "ReferenceType", "ReferenceId", "Duration", "FileType", "ExtraProperties",
    ];

    private static readonly PropertyInfo? ReferenceType = Find("ReferenceType");
    private static readonly PropertyInfo? ReferenceId = Find("ReferenceId");
    private static readonly PropertyInfo? Duration = Find("Duration");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Captures the allow-listed fields so a failed update can put them back on the tracked instance.</summary>
    public static Action Snapshot(TFile file)
    {
        var title = file.Title;
        var comment = file.Comment;
        var referenceType = ReferenceType?.GetValue(file);
        var referenceId = ReferenceId?.GetValue(file);
        var duration = Duration?.GetValue(file);
        var extra = file.ExtraProperties is null ? null : new Dictionary<string, object>(file.ExtraProperties);
        return () =>
        {
            file.Title = title;
            file.Comment = comment;
            ReferenceType?.SetValue(file, referenceType);
            ReferenceId?.SetValue(file, referenceId);
            Duration?.SetValue(file, duration);
            file.ExtraProperties = extra!;
        };
    }

    /// <summary>Reads the requested <c>FileType</c> of a JSON document without applying anything.</summary>
    public static FileType? ReadFileType(JsonObject json)
    {
        foreach (var (name, node) in json)
        {
            if (string.Equals(name, "FileType", StringComparison.OrdinalIgnoreCase))
            {
                return node is null ? null : Convert<FileType>(node, name);
            }
        }

        return null;
    }

    public static bool Has(string name) => typeof(TFile).GetProperty(name) is not null;

    /// <summary>Copies the allow-listed fields of a whole-entity update. A null member keeps the current value (clear one with a JSON patch).</summary>
    public static void CopyFrom(TFile target, TFile source, bool allowExtraProperties)
    {
        if (source.Title is not null)
        {
            target.Title = source.Title;
        }

        if (source.Comment is not null)
        {
            target.Comment = source.Comment;
        }

        CopyProperty(ReferenceType, target, source, skipNull: true);
        CopyProperty(ReferenceId, target, source, skipNull: true);
        CopyProperty(Duration, target, source, skipNull: true);
        if (allowExtraProperties)
        {
            MergeExtra(target, source.ExtraProperties);
        }
    }

    /// <summary>Applies the allow-listed members of a JSON document; every other member is ignored.</summary>
    /// <returns>The requested file type, when the document carries one.</returns>
    public static FileType? ApplyJson(TFile target, JsonObject json, bool allowExtraProperties)
    {
        FileType? requested = null;
        foreach (var (name, node) in json)
        {
            switch (name.ToLowerInvariant())
            {
                case "title":
                    if (node is not null)
                    {
                        target.Title = ReadString(node, name);
                    }

                    break;
                case "comment":
                    target.Comment = node is null ? string.Empty : ReadString(node, name);
                    break;
                case "referencetype":
                    SetFromJson(ReferenceType, target, node, name, nullToEmpty: true);
                    break;
                case "referenceid":
                    SetFromJson(ReferenceId, target, node, name, nullToEmpty: false);
                    break;
                case "duration":
                    SetFromJson(Duration, target, node, name, nullToEmpty: false);
                    break;
                case "filetype":
                    if (node is not null)
                    {
                        requested = Convert<FileType>(node, name);
                    }

                    break;
                case "extraproperties":
                    if (allowExtraProperties && node is JsonObject extra)
                    {
                        foreach (var (key, value) in extra)
                        {
                            target.ExtraProperties ??= new Dictionary<string, object>();
                            target.ExtraProperties[key] = value is null ? null! : Convert<object>(value, key);
                        }
                    }

                    break;
            }
        }

        return requested;
    }

    public static void EnsureRequiredStrings(TFile file, string fallbackTitle)
    {
        file.Title ??= fallbackTitle;
        file.Comment ??= string.Empty;
        if (ReferenceType is not null && ReferenceType.GetValue(file) is null && ReferenceType.PropertyType == typeof(string))
        {
            ReferenceType.SetValue(file, string.Empty);
        }
    }

    public static string? GetReferenceType(TFile file) => ReferenceType?.GetValue(file) as string;

    /// <summary>Builds the list filter: enabled rows, query fields, then the host scope.</summary>
    public static Expression<Func<TFile, bool>> BuildFilter(FileListQuery query, Expression<Func<TFile, bool>>? scope)
    {
        var parameter = Expression.Parameter(typeof(TFile), "f");
        Expression body = Expression.Constant(true);

        void And(Expression condition) => body = Expression.AndAlso(body, condition);

        And(Expression.Equal(MemberOf(parameter, "IsEnabled"), Expression.Constant(true)));

        if (query.ReferenceType is not null)
        {
            var member = RequireMember(parameter, "ReferenceType");
            And(Expression.Equal(member, ConstantFor(member, query.ReferenceType)));
        }

        if (query.ReferenceId is { } referenceId)
        {
            var member = RequireMember(parameter, "ReferenceId");
            And(Expression.Equal(member, ConstantFor(member, referenceId)));
        }

        if (query.FileType is { } fileType)
        {
            And(Expression.Equal(MemberOf(parameter, "FileType"), Expression.Constant(fileType)));
        }

        if (scope is not null)
        {
            And(new ParameterReplacer(scope.Parameters[0], parameter).Visit(scope.Body)!);
        }

        return Expression.Lambda<Func<TFile, bool>>(body, parameter);
    }

    // The consumer entity decides the member's type (string or enum reference type, long or long? reference id): the constant is
    // converted to it, and a value the member cannot hold is a 400 instead of an expression-tree exception (500).
    private static ConstantExpression ConstantFor(MemberExpression member, object value)
    {
        var memberType = member.Type;
        var underlying = Nullable.GetUnderlyingType(memberType) ?? memberType;
        object? converted = null;
        try
        {
            if (underlying.IsEnum)
            {
                if (value is string text && Enum.TryParse(underlying, text, ignoreCase: true, out var parsed) &&
                    Enum.IsDefined(underlying, parsed!))
                {
                    converted = parsed;
                }
            }
            else if (underlying == value.GetType())
            {
                converted = value;
            }
            else if (value is long && underlying.IsPrimitive && underlying != typeof(bool) && underlying != typeof(char))
            {
                converted = System.Convert.ChangeType(value, underlying, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex) when (ex is OverflowException or InvalidCastException or FormatException)
        {
            converted = null;
        }

        return converted is null
            ? throw new ApiException($"Filtering by {member.Member.Name} with this value is not supported for this file type.", null,
                LogLevel.Warning, null, HttpStatusCode.BadRequest, "FILTER_NOT_SUPPORTED")
            : Expression.Constant(converted, memberType);
    }

    private static MemberExpression MemberOf(ParameterExpression parameter, string name) =>
        typeof(TFile).GetProperty(name) is { } property
            ? Expression.Property(parameter, property)
            : Expression.Property(Expression.Convert(parameter, typeof(IFile)), name);

    private static MemberExpression RequireMember(ParameterExpression parameter, string name)
    {
        if (typeof(TFile).GetProperty(name) is not { } property)
        {
            throw new ApiException($"Filtering by {name} is not supported for this file type.", null, LogLevel.Warning,
                null, HttpStatusCode.BadRequest, "FILTER_NOT_SUPPORTED");
        }

        return Expression.Property(parameter, property);
    }

    private static PropertyInfo? Find(string name)
    {
        var property = typeof(TFile).GetProperty(name);
        return property is { CanRead: true, CanWrite: true } ? property : null;
    }

    private static void CopyProperty(PropertyInfo? property, TFile target, TFile source, bool skipNull)
    {
        if (property is null)
        {
            return;
        }

        var value = property.GetValue(source);
        if (value is null && skipNull)
        {
            return;
        }

        property.SetValue(target, value);
    }

    private static void MergeExtra(TFile target, IDictionary<string, object>? extra)
    {
        if (extra is null)
        {
            return;
        }

        target.ExtraProperties ??= new Dictionary<string, object>();
        foreach (var pair in extra)
        {
            target.ExtraProperties[pair.Key] = pair.Value;
        }
    }

    private static void SetFromJson(PropertyInfo? property, TFile target, JsonNode? node, string name, bool nullToEmpty)
    {
        if (property is null)
        {
            return;
        }

        if (node is null)
        {
            property.SetValue(target, nullToEmpty ? string.Empty : null);
            return;
        }

        try
        {
            property.SetValue(target, node.Deserialize(property.PropertyType, JsonOptions));
        }
        catch (JsonException e)
        {
            throw Invalid(name, e);
        }
    }

    private static string ReadString(JsonNode node, string name)
    {
        try
        {
            return node.GetValue<string>();
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            throw Invalid(name, e);
        }
    }

    private static T Convert<T>(JsonNode node, string name)
    {
        try
        {
            return node.Deserialize<T>(JsonOptions)!;
        }
        catch (JsonException e)
        {
            throw Invalid(name, e);
        }
    }

    private static ApiException Invalid(string name, Exception inner) =>
        new($"The value of '{name}' is not valid.", inner, LogLevel.Warning, null, HttpStatusCode.BadRequest,
            "INVALID_FIELD_VALUE");

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
