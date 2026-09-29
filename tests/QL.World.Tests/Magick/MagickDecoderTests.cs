using System.IO;
using ImageMagick;
using QL.World.Data;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Nas;
using QL.World.Tests.Support;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QL.World.Tests;

/// <summary>
/// M4-C：TGA/EXR/HDR/PSD 四专业格式。样例文件由 Magick.NET 程序化生成，
/// 解码链路为 ImageInspector（元数据）与 ThumbnailService（缩略图）。
/// </summary>
public sealed class MagickDecoderTests : IDisposable
{
    private readonly string _directory;
    private readonly AppSettingsService _settings;
    private readonly ImageInspector _inspector = new();

    public MagickDecoderTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "ql-world-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _settings = new AppSettingsService(_directory);
        _settings.Load();
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* Best effort. */ }
    }

    /// <summary>Generates a solid red sample of the given format (7×5).</summary>
    private string CreateSample(string name, MagickFormat format)
    {
        string path = Path.Combine(_directory, name);
        using var image = new MagickImage(MagickColors.Red, 7, 5);
        image.Format = format;
        image.Write(path);
        return path;
    }

    public static TheoryData<string, MagickFormat> FormatCases => new()
    {
        { "sample.tga", MagickFormat.Tga },
        { "sample.exr", MagickFormat.Exr },
        { "sample.hdr", MagickFormat.Hdr },
        { "sample.psd", MagickFormat.Psd },
    };

    [Theory]
    [MemberData(nameof(FormatCases))]
    public void Inspector_reads_metadata_for_professional_formats(
        string name, MagickFormat format)
    {
        string path = CreateSample(name, format);

        bool ok = _inspector.TryReadMetadata(path, out int width, out int height, out string? fmt);

        Assert.True(ok, $"应能读取 {name} 的元数据");
        Assert.Equal(7, width);
        Assert.Equal(5, height);
        Assert.False(string.IsNullOrEmpty(fmt));
    }

    [Theory]
    [MemberData(nameof(FormatCases))]
    public void Decoder_loads_red_composite_frame(string name, MagickFormat format)
    {
        string path = CreateSample(name, format);
        var decoder = new MagickDecoder();

        bool ok = decoder.TryLoadFrame(path, out Image? frame);

        Assert.True(ok, $"应能解码 {name}");
        Assert.NotNull(frame);
        using (frame)
        {
            Assert.Equal(7, frame.Width);
            Assert.Equal(5, frame.Height);
            var rgba = Assert.IsType<Image<Rgba32>>(frame);
            Rgba32 pixel = rgba[3, 2];
            // Q8 非 HDRI 自动 8bit；EXR/HDR 经线性↔sRGB 往返允许少量漂移。
            Assert.True(pixel.R >= 240, $"R 通道应接近 255（实际 {pixel.R}）");
            Assert.True(pixel.G <= 15 && pixel.B <= 15,
                $"G/B 通道应接近 0（实际 {pixel.G}/{pixel.B}）");
        }
    }

    [Fact]
    public void Thumbnail_pipeline_decodes_tga_and_psd()
    {
        using var service = new ThumbnailService(_directory, _settings);
        string tga = CreateSample("thumb.tga", MagickFormat.Tga);
        string psd = CreateSample("thumb.psd", MagickFormat.Psd);

        service.Request("tga000000000001", new LocalImageSource(tga));
        service.Request("psd000000000001", new LocalImageSource(psd));

        Assert.True(WaitFor(
            () => File.Exists(service.GetThumbnailPath("tga000000000001")),
            TimeSpan.FromSeconds(15)));
        Assert.True(WaitFor(
            () => File.Exists(service.GetThumbnailPath("psd000000000001")),
            TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public async Task Corrupt_professional_file_fails_both_decoders_and_is_skipped()
    {
        string garbagePath = Path.Combine(_directory, "garbage.tga");
        File.WriteAllBytes(garbagePath, [0x13, 0x7C, 0x00, 0x9F, 0xFF, 0x00, 0x01]);

        Assert.False(_inspector.TryReadMetadata(garbagePath, out _, out _, out _));

        // 导入链路：垃圾文件按跳过计数，不影响同目录正常文件。
        using var library = new TestLibrary();
        string goodPath = TestImageFactory.CreatePng(_directory, "good.png");
        var import = new ImportService(library.Db, _inspector);
        ImportReport report = await import.ImportAsync(new ImportOptions
        {
            InputPaths = [_directory],
            RecurseSubfolders = false,
        });

        Assert.Equal(1, report.AddedCount);
        Assert.Equal(1, report.SkippedCount);
        Assert.Equal(goodPath, Assert.Single(report.Added).FilePath);
    }

    [Fact]
    public void Imagesharp_formats_still_probe_normally()
    {
        string png = TestImageFactory.CreatePng(_directory, "regress.png", 16, 9);

        bool ok = _inspector.TryReadMetadata(png, out int width, out int height, out string? fmt);

        Assert.True(ok);
        Assert.Equal(16, width);
        Assert.Equal(9, height);
        Assert.Equal("png", fmt);
    }

    [Fact]
    public void Supported_extensions_cover_the_four_professional_formats()
    {
        Assert.Contains(".tga", ImportOptions.SupportedExtensions);
        Assert.Contains(".exr", ImportOptions.SupportedExtensions);
        Assert.Contains(".hdr", ImportOptions.SupportedExtensions);
        Assert.Contains(".psd", ImportOptions.SupportedExtensions);
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }
}
