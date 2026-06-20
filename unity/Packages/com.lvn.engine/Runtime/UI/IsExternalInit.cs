// Polyfill for C# 9 `init`-only setters (and records). Unity's netstandard2.1
// profile does not ship System.Runtime.CompilerServices.IsExternalInit, so any
// `init` accessor fails to compile with CS0518 without this shim. Declared
// `internal` so it never collides with another assembly's copy of the same type.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
