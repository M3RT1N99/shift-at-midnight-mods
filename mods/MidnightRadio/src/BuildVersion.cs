namespace MidnightRadio
{
    /// <summary>
    /// The single place the mod's version is written.
    ///
    /// It has to be a compile-time constant because MelonInfo is an attribute, so it cannot
    /// be read from the assembly or the manifest at runtime. That is exactly how it drifted:
    /// the manifest and csproj said 1.1.0 while the loader kept reporting 1.0.0. Keeping it
    /// here means one edit, and scripts/build.ps1 checks it against mod.json.
    /// </summary>
    internal static class BuildVersion
    {
        public const string Value = "1.2.4";
    }
}
