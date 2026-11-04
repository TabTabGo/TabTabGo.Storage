using TabTabGo.Storage.ContentTypes;
using Xunit;

namespace TabTabGo.Storage.Tests;

public class ContentTypeMapTests
{
    [Theory]
    [InlineData("png", "image/png")]
    [InlineData(".PNG", "image/png")]
    [InlineData(".jpg", "image/jpeg")]
    [InlineData("pdf", "application/pdf")]
    [InlineData(".txt", "text/plain")]
    [InlineData(".mp4", "video/mp4")]
    [InlineData(".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData(".unknownext", "application/octet-stream")]
    [InlineData("", "application/octet-stream")]
    [InlineData(null, "application/octet-stream")]
    public void FromExtension_MapsOrDefaults(string? extension, string expected) =>
        Assert.Equal(expected, ContentTypeMap.FromExtension(extension));

    [Fact]
    public void TryGet_KnownAndUnknown()
    {
        Assert.True(ContentTypeMap.TryGet(".gif", out var gif));
        Assert.Equal("image/gif", gif);
        Assert.False(ContentTypeMap.TryGet(".nope", out var none));
        Assert.Null(none);
        Assert.False(ContentTypeMap.TryGet(null, out _));
    }

    // X-1: active content must never be served with its native, script-capable type.
    [Theory]
    [InlineData(".html")]
    [InlineData(".htm")]
    [InlineData(".xhtml")]
    [InlineData(".xht")]
    [InlineData(".svg")]
    [InlineData(".xml")]
    [InlineData(".xsl")]
    [InlineData(".xslt")]
    [InlineData(".js")]
    [InlineData(".mjs")]
    public void FromExtension_ActiveContent_IsOctetStream_NotNativeType(string extension)
    {
        Assert.Equal("application/octet-stream", ContentTypeMap.FromExtension(extension));
        Assert.True(ContentTypeMap.IsActiveContent(extension));
    }

    [Theory]
    [InlineData("image/png", true)]
    [InlineData("IMAGE/PNG", true)]
    [InlineData("application/pdf", true)]
    [InlineData("text/plain", true)]
    [InlineData("audio/mpeg", true)]
    [InlineData("video/mp4", true)]
    [InlineData("image/jpeg; charset=x", false)]
    [InlineData("image/png;charset=utf-8", false)]
    [InlineData("image/png,text/html", false)]
    [InlineData("image/png\r\nX-Evil: 1", false)]
    [InlineData("image/png\"", false)]
    [InlineData(" image/png", false)]
    [InlineData("image/png ", false)]
    [InlineData("image/svg+xml", false)]
    [InlineData("text/html", false)]
    [InlineData("application/xml", false)]
    [InlineData("application/octet-stream", false)]
    [InlineData("text/javascript", false)]
    [InlineData("image/", false)]
    [InlineData("image/unknown", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSafeToRender_ExactAllowListOnly(string? contentType, bool expected) =>
        Assert.Equal(expected, ContentTypeMap.IsSafeToRender(contentType));

    // X-5: the caller's string is never echoed; the extension decides.
    [Theory]
    [InlineData("image/png", "a/b.png", "image/png")]
    [InlineData("text/html", "a/b.png", "image/png")]
    [InlineData("image/png,text/html", "a/b.png", "image/png")]
    [InlineData("image/png\r\nX-Evil: 1", "a/b.png", "image/png")]
    [InlineData("image/png\"", "a/b.png", "image/png")]
    [InlineData("image/png;charset=utf-8", "a/b.png", "image/png")]
    [InlineData("IMAGE/PNG", "a/b.png", "image/png")]
    [InlineData("image/svg+xml", "a/b.png", "image/png")]
    [InlineData(null, "a/b.pdf", "application/pdf")]
    [InlineData("image/png", "a/evil.html", "application/octet-stream")]
    [InlineData("image/png", "a/evil.svg", "application/octet-stream")]
    [InlineData("text/html", "a/noext", "application/octet-stream")]
    public void ChooseResponseType_AlwaysTheExtensionType(string? requested, string path, string expected) =>
        Assert.Equal(expected, ContentTypeMap.ChooseResponseType(requested, path));

    [Theory]
    [InlineData("image/png", true)]
    [InlineData("application/pdf", true)]
    [InlineData("video/mp4", true)]
    [InlineData("text/plain", false)]
    [InlineData("image/svg+xml", false)]
    [InlineData("text/html", false)]
    [InlineData(null, false)]
    public void IsSafeToRenderSigned_ExcludesTextPlain(string? contentType, bool expected) =>
        Assert.Equal(expected, ContentTypeMap.IsSafeToRenderSigned(contentType));

    [Fact]
    public void IsActiveContent_PlainTypesAreNot()
    {
        Assert.False(ContentTypeMap.IsActiveContent(".png"));
        Assert.False(ContentTypeMap.IsActiveContent(null));
    }
}

public class ContentDispositionFormatterTests
{
    [Fact]
    public void Format_SimpleName_HasAsciiAndEncodedForms() =>
        Assert.Equal("attachment; filename=\"report.pdf\"; filename*=UTF-8''report.pdf",
            ContentDispositionFormatter.Format("report.pdf"));

    [Fact]
    public void Format_Inline_UsesInline() =>
        Assert.StartsWith("inline; ", ContentDispositionFormatter.Format("a.png", inline: true));

    [Fact]
    public void Format_Unicode_IsPercentEncodedAndAsciiFallback()
    {
        var value = ContentDispositionFormatter.Format("résumé.pdf");

        Assert.Contains("filename=\"r_sum_.pdf\"", value);
        Assert.Contains("filename*=UTF-8''r%C3%A9sum%C3%A9.pdf", value);
    }

    [Fact]
    public void Format_QuotesAndCrlf_CannotInjectOrBreakOut()
    {
        var value = ContentDispositionFormatter.Format("a\"b\r\nX-Evil: 1.png");

        Assert.DoesNotContain('\r', value);
        Assert.DoesNotContain('\n', value);
        Assert.DoesNotContain("\"b", value);
        Assert.StartsWith("attachment; filename=\"", value);
    }

    [Fact]
    public void Format_PathSegments_AreDropped() =>
        Assert.Contains("filename=\"passwd\"", ContentDispositionFormatter.Format("..\\..\\etc/passwd"));

    [Fact]
    public void Format_BidiControlCharacters_AreRemoved()
    {
        var value = ContentDispositionFormatter.Format("invoice\u202Egnp.exe\u2066x\u2069\u200E.pdf");

        Assert.DoesNotContain("\u202E", value, StringComparison.Ordinal);
        Assert.DoesNotContain("\u2066", value, StringComparison.Ordinal);
        Assert.DoesNotContain("\u2069", value, StringComparison.Ordinal);
        Assert.DoesNotContain("%E2%80%AE", value, StringComparison.Ordinal);
        Assert.Contains("filename=\"invoicegnp.exex.pdf\"", value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\r\n")]
    public void Format_NoUsableName_ReturnsBareDisposition(string? name) =>
        Assert.Equal("attachment", ContentDispositionFormatter.Format(name));
}
