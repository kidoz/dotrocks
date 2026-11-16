namespace DotRocks.PackageTests;

internal static class PackageTestFramework
{
#if NET11_0
    public const string Current = "net11.0";
#else
    public const string Current = "net10.0";
#endif
    public static readonly string[] Supported = ["net10.0", "net11.0"];
}
