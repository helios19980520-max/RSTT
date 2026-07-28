using Xunit;

namespace RSTT.Speech.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NativeInputTestGroup
{
    public const string Name = "Native Win32 input";
}
