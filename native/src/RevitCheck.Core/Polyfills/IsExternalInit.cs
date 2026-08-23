namespace System.Runtime.CompilerServices;

// netstandard2.0 predates C#'s `init` accessors and doesn't ship the marker
// type the compiler expects for them. This is the standard, widely-used
// polyfill - a compile-time-only marker, no runtime behavior - that lets
// `init` be used on netstandard2.0 without pulling in a NuGet package for it.
internal static class IsExternalInit { }
