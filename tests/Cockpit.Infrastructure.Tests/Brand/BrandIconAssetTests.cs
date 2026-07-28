using System.Security.Cryptography;
using SkiaSharp;

namespace Cockpit.Infrastructure.Tests.Brand;

/// <summary>
/// <c>scripts/generate-brand-icons.sh</c> (AC-432) promises one geometry in two colours: every blue/teal pair
/// shares its alpha channel byte-for-byte, and only the colour differs. That promise is invisible in a diff — a
/// script edit or a hand-replaced file can break it while every file still opens fine — so these tests pin the
/// square sizes, the shared alpha, the colour difference, the transparent border, and the .ico container that the
/// generator's own comments and <c>brand/README.md</c> describe.
/// </summary>
public sealed class BrandIconAssetTests
{
    // Measured as the mean R/G/B over fully opaque pixels on the four master renders (see the AC-432 review):
    // blue's B/G ratio sits at ~1.95-1.98, teal's at ~1.22-1.23. The thresholds below sit roughly midway between
    // those two clusters with wide margin either side, so a genuine blue or teal render clears them comfortably
    // while a colour swap between the two sets fails both.
    private const double _BlueMinBlueOverGreen = 1.6;
    private const double _TealMaxBlueOverGreen = 1.4;

    // Pinned when brand/ was last generated (AC-432 review, item 3). If this fails, the source render changed:
    // re-run scripts/generate-brand-icons.sh and replace this constant with the hash it reports.
    private const string _BrandMarkSourceSha256 = "a4396ab8b9e38565e3cbdbeba3a2e56015c4d3b936f0b5783fda8d3c594ed2d2";

    private static readonly int[] _ladderSizes = [16, 24, 32, 48, 64, 128, 256, 512];
    private static readonly int[] _icoSizes = [16, 24, 32, 48, 64, 128, 256];
    private static readonly byte[] _pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly (string RelativePath, int Size)[] _assets = _BuildAssetList();

    [Theory]
    [MemberData(nameof(Assets))]
    public void EachAsset_IsSquareAtItsPromisedSize(string relativePath, int expectedSize)
    {
        var image = _Load(_Path(relativePath));

        Assert.Equal(expectedSize, image.Width);
        Assert.Equal(expectedSize, image.Height);
    }

    [Theory]
    [MemberData(nameof(ColourPairs))]
    public void EachColourPair_SharesIdenticalAlphaInEveryPixel(string bluePath, string tealPath)
    {
        var blue = _Load(_Path(bluePath));
        var teal = _Load(_Path(tealPath));

        Assert.Equal(blue.Width, teal.Width);
        Assert.Equal(blue.Height, teal.Height);

        for (var y = 0; y < blue.Height; y++)
        {
            for (var x = 0; x < blue.Width; x++)
            {
                Assert.Equal(blue.At(x, y).A, teal.At(x, y).A);
            }
        }
    }

    [Theory]
    [MemberData(nameof(ColourPairs))]
    public void EachColourPair_DiffersInRgbSomewhere(string bluePath, string tealPath)
    {
        var blue = _Load(_Path(bluePath));
        var teal = _Load(_Path(tealPath));

        var differs = false;
        for (var y = 0; y < blue.Height && !differs; y++)
        {
            for (var x = 0; x < blue.Width; x++)
            {
                var (r1, g1, b1, _) = blue.At(x, y);
                var (r2, g2, b2, _) = teal.At(x, y);
                if (r1 != r2 || g1 != g2 || b1 != b2)
                {
                    differs = true;
                    break;
                }
            }
        }

        Assert.True(differs, $"{bluePath} and {tealPath} have identical RGB in every pixel");
    }

    [Theory]
    [MemberData(nameof(AssetPaths))]
    public void EachAsset_HasTransparentCornersAndAtLeastOneOpaquePixel(string relativePath)
    {
        var image = _Load(_Path(relativePath));
        var w = image.Width;
        var h = image.Height;

        Assert.Equal(0, image.At(0, 0).A);
        Assert.Equal(0, image.At(w - 1, 0).A);
        Assert.Equal(0, image.At(0, h - 1).A);
        Assert.Equal(0, image.At(w - 1, h - 1).A);

        var hasOpaquePixel = false;
        for (var y = 0; y < h && !hasOpaquePixel; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (image.At(x, y).A == 255)
                {
                    hasOpaquePixel = true;
                    break;
                }
            }
        }

        Assert.True(hasOpaquePixel, $"{relativePath} has no fully opaque pixel");
    }

    [Theory]
    [MemberData(nameof(AssetPaths))]
    public void EachAsset_FullyTransparentPixelsCarryNoColour(string relativePath)
    {
        var image = _Load(_Path(relativePath));

        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var (r, g, b, a) = image.At(x, y);
                if (a == 0)
                {
                    Assert.Equal(0, r);
                    Assert.Equal(0, g);
                    Assert.Equal(0, b);
                }
            }
        }
    }

    [Theory]
    [InlineData("blue")]
    [InlineData("teal")]
    public void TheIcoFile_ContainsThePngLadderUpToTwoFiftySixInOrder(string colour)
    {
        var bytes = File.ReadAllBytes(_Path(Path.Combine("icons", $"wispslate-{colour}.ico")));

        Assert.Equal(0, _ReadUInt16(bytes, 0));
        Assert.Equal(1, _ReadUInt16(bytes, 2));
        Assert.Equal(_icoSizes.Length, _ReadUInt16(bytes, 4));

        for (var i = 0; i < _icoSizes.Length; i++)
        {
            var entry = 6 + (i * 16);
            var width = bytes[entry] == 0 ? 256 : bytes[entry];
            var height = bytes[entry + 1] == 0 ? 256 : bytes[entry + 1];
            var size = _ReadUInt32(bytes, entry + 8);
            var offset = _ReadUInt32(bytes, entry + 12);

            Assert.Equal(_icoSizes[i], width);
            Assert.Equal(_icoSizes[i], height);
            Assert.True((long)offset + size <= bytes.Length, $"entry {i} of {colour}.ico falls outside the file");

            var payload = bytes[(int)offset..(int)(offset + size)];
            Assert.Equal(_pngSignature, payload[..8]);

            // The signature check above only proves the payload is *a* PNG. The ladder promises entry i is
            // specifically brand/icons/<colour>/<size>.png — comparing the whole payload against that file
            // catches a directory that declares the right sizes while every entry actually carries the same
            // (e.g. 16x16) image, which the signature and container checks alone let through.
            var expected = File.ReadAllBytes(_Path(Path.Combine("icons", colour, $"{_icoSizes[i]}.png")));
            Assert.Equal(expected, payload);
        }
    }

    [Theory]
    [MemberData(nameof(ColourMasters))]
    public void TheColourMaster_HasThePromisedHue(string relativePath, bool isBlue)
    {
        var image = _Load(_Path(relativePath));

        long green = 0;
        long blue = 0;
        long opaqueCount = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var (_, g, b, a) = image.At(x, y);
                if (a != 255)
                {
                    continue;
                }

                green += g;
                blue += b;
                opaqueCount++;
            }
        }

        Assert.True(opaqueCount > 0, $"{relativePath} has no fully opaque pixel to measure");

        var averageGreen = (double)green / opaqueCount;
        var averageBlue = (double)blue / opaqueCount;

        if (isBlue)
        {
            Assert.True(
                averageBlue >= _BlueMinBlueOverGreen * averageGreen,
                $"{relativePath}: mean B ({averageBlue:F1}) is not at least {_BlueMinBlueOverGreen}x mean G " +
                $"({averageGreen:F1}) — this looks like teal, not blue");
        }
        else
        {
            Assert.True(
                averageBlue <= _TealMaxBlueOverGreen * averageGreen,
                $"{relativePath}: mean B ({averageBlue:F1}) exceeds {_TealMaxBlueOverGreen}x mean G " +
                $"({averageGreen:F1}) — this looks like blue, not teal");
        }
    }

    [Fact]
    public void TheBrandMarkSource_MatchesTheHashTheIconSetWasGeneratedFrom()
    {
        using var stream = File.OpenRead(_BrandMarkSourcePath());
        var actual = Convert.ToHexStringLower(SHA256.HashData(stream));

        Assert.True(
            actual == _BrandMarkSourceSha256,
            $"src/Cockpit.App/Assets/BrandMark.png has changed (sha256 now {actual}) — everything under brand/ " +
            "was generated from a different render. Re-run scripts/generate-brand-icons.sh and replace " +
            $"{nameof(_BrandMarkSourceSha256)} in this file with the hash it reports.");
    }

    public static TheoryData<string, int> Assets()
    {
        var data = new TheoryData<string, int>();
        foreach (var (path, size) in _assets)
        {
            data.Add(path, size);
        }

        return data;
    }

    public static TheoryData<string> AssetPaths()
    {
        var data = new TheoryData<string>();
        foreach (var (path, _) in _assets)
        {
            data.Add(path);
        }

        return data;
    }

    public static TheoryData<string, string> ColourPairs()
    {
        var data = new TheoryData<string, string>
        {
            { "wispslate-mark-blue.png", "wispslate-mark-teal.png" },
            { "wispslate-icon-blue.png", "wispslate-icon-teal.png" },
        };

        foreach (var size in _ladderSizes)
        {
            data.Add(Path.Combine("icons", "blue", $"{size}.png"), Path.Combine("icons", "teal", $"{size}.png"));
        }

        return data;
    }

    public static TheoryData<string, bool> ColourMasters() => new()
    {
        { "wispslate-mark-blue.png", true },
        { "wispslate-icon-blue.png", true },
        { "wispslate-mark-teal.png", false },
        { "wispslate-icon-teal.png", false },
    };

    private static (string, int)[] _BuildAssetList()
    {
        var list = new List<(string, int)>
        {
            ("wispslate-mark-blue.png", 512),
            ("wispslate-mark-teal.png", 512),
            ("wispslate-icon-blue.png", 1024),
            ("wispslate-icon-teal.png", 1024),
        };

        foreach (var colour in new[] { "blue", "teal" })
        {
            foreach (var size in _ladderSizes)
            {
                list.Add((Path.Combine("icons", colour, $"{size}.png"), size));
            }
        }

        return [.. list];
    }

    // Decoded through SKCodec into an explicit Rgba8888/Unpremul SKImageInfo rather than SKBitmap.Decode's default
    // buffer: Skia's default decode is premultiplied, which rewrites every fully-transparent pixel's RGB to 0
    // regardless of what the file stores — exactly the bytes EachAsset_FullyTransparentPixelsCarryNoColour and the
    // other pixel-level tests here need to read as they actually are on disk.
    private static _RawImage _Load(string path)
    {
        using var codec = SKCodec.Create(path) ?? throw new InvalidOperationException($"could not decode {path}");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pixels = new byte[info.BytesSize];
        var result = codec.GetPixels(info, pixels);
        if (result != SKCodecResult.Success)
        {
            throw new InvalidOperationException($"failed to decode {path}: {result}");
        }

        return new _RawImage(info.Width, info.Height, pixels);
    }

    private static string _Path(string relativeToBrand) => Path.Combine(_BrandRoot(), relativeToBrand);

    private static string _BrandRoot() => Path.Combine(_RepoRoot(Path.Combine("brand", "wispslate-icon-blue.png")), "brand");

    private static string _BrandMarkSourcePath()
    {
        var relative = Path.Combine("src", "Cockpit.App", "Assets", "BrandMark.png");
        return Path.Combine(_RepoRoot(relative), relative);
    }

    // Walks up from the test output looking for a repo checkout that holds the given file, relative to its root.
    // Shared by _BrandRoot (which looks for brand/wispslate-icon-blue.png) and _BrandMarkSourcePath (which looks
    // for the render everything under brand/ was generated from), so the two markers cannot drift onto two
    // different notions of "repo root".
    private static string _RepoRoot(string markerRelativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, markerRelativePath)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"No folder above the test output holds {markerRelativePath} — this test reads the repo it belongs to.");
    }

    private static ushort _ReadUInt16(byte[] bytes, int offset) => (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

    private static uint _ReadUInt32(byte[] bytes, int offset) =>
        (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));

    /// <summary>Decoded pixels as straight (unpremultiplied) RGBA bytes, one row after another.</summary>
    private sealed class _RawImage(int width, int height, byte[] pixels)
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public (byte R, byte G, byte B, byte A) At(int x, int y)
        {
            var offset = ((y * Width) + x) * 4;
            return (pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
        }
    }
}
