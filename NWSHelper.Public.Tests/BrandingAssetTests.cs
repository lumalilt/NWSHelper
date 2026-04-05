using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace NWSHelper.Tests;

public class BrandingAssetTests
{
    private static readonly int[] AppListTargetSizes = [16, 20, 24, 30, 32, 36, 40, 44, 48, 60, 64, 72, 80, 96, 256];
    private static readonly int[] ExpectedWin32IconSizes = [16, 24, 32, 48, 64, 128, 256];

    [Fact]
    public void GuiProject_EmbedsWindowsApplicationIcon()
    {
        var projectPath = Path.Combine(GetRepositoryRoot(), "NWSHelper.Gui", "NWSHelper.Gui.csproj");
        Assert.True(File.Exists(projectPath), $"Expected project at {projectPath}");

        var project = File.ReadAllText(projectPath);

        Assert.Contains("<ApplicationIcon>Assets\\nwsh_multi.ico</ApplicationIcon>", project, StringComparison.Ordinal);
    }

    [Fact]
    public void GuiMultiIcon_ProvidesSquareFramesAtExpectedSizes()
    {
        var iconPath = Path.Combine(GetRepositoryRoot(), "NWSHelper.Gui", "Assets", "nwsh_multi.ico");
        Assert.True(File.Exists(iconPath), $"Expected icon at {iconPath}");

        using var stream = File.OpenRead(iconPath);
        using var reader = new BinaryReader(stream);

        Assert.Equal((ushort)0, reader.ReadUInt16());
        Assert.Equal((ushort)1, reader.ReadUInt16());

        var imageCount = reader.ReadUInt16();
        Assert.True(imageCount >= ExpectedWin32IconSizes.Length, $"Expected at least {ExpectedWin32IconSizes.Length} icon frames but found {imageCount}.");

        var sizes = new HashSet<int>();

        for (var index = 0; index < imageCount; index++)
        {
            var width = NormalizeIconDimension(reader.ReadByte());
            var height = NormalizeIconDimension(reader.ReadByte());

            Assert.Equal(width, height);

            _ = reader.ReadByte();
            _ = reader.ReadByte();
            _ = reader.ReadUInt16();
            _ = reader.ReadUInt16();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();

            sizes.Add(width);
        }

        foreach (var expectedSize in ExpectedWin32IconSizes)
        {
            Assert.Contains(expectedSize, sizes);
        }
    }

    [Fact]
    public void InstallerScript_UsesExplicitSetupIconDefine()
    {
        var scriptPath = Path.Combine(GetRepositoryRoot(), "scripts", "inno", "NWSHelper.iss");
        Assert.True(File.Exists(scriptPath), $"Expected script at {scriptPath}");

        var script = File.ReadAllText(scriptPath);

        Assert.Contains("#ifndef SetupIconFile", script, StringComparison.Ordinal);
        Assert.Contains("SetupIconFile={#SetupIconFile}", script, StringComparison.Ordinal);
    }

    [Fact]
    public void MsixAssets_ProvideRequiredAppListIconVariants()
    {
        var assetsDirectory = Path.Combine(GetRepositoryRoot(), "scripts", "msix", "Assets");
        Assert.True(Directory.Exists(assetsDirectory), $"Expected MSIX assets directory at {assetsDirectory}");

        Assert.True(File.Exists(Path.Combine(assetsDirectory, "Square44x44Logo.png")));
        Assert.True(File.Exists(Path.Combine(assetsDirectory, "Square44x44Logo_altform-unplated.png")));
        Assert.True(File.Exists(Path.Combine(assetsDirectory, "Square44x44Logo_altform-unplated_contrast-white.png")));
        Assert.True(File.Exists(Path.Combine(assetsDirectory, "Square44x44Logo_altform-lightunplated.png")));

        foreach (var size in AppListTargetSizes)
        {
            Assert.True(File.Exists(Path.Combine(assetsDirectory, $"Square44x44Logo.targetsize-{size}.png")));
            Assert.True(File.Exists(Path.Combine(assetsDirectory, $"Square44x44Logo.targetsize-{size}_altform-unplated.png")));
            Assert.True(File.Exists(Path.Combine(assetsDirectory, $"Square44x44Logo.targetsize-{size}_altform-unplated_contrast-white.png")));
            Assert.True(File.Exists(Path.Combine(assetsDirectory, $"Square44x44Logo.targetsize-{size}_altform-lightunplated.png")));
            Assert.False(File.Exists(Path.Combine(assetsDirectory, $"Square44x44Logo.altform-unplated_targetsize-{size}.png")));
        }
    }

    private static string GetRepositoryRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }

    private static int NormalizeIconDimension(byte value)
    {
        return value == 0 ? 256 : value;
    }
}