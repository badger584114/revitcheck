namespace System.Runtime.CompilerServices;

// net48 predates C#'s `init` accessors and doesn't ship the marker type the
// compiler expects for them - same situation and same standard polyfill as
// RevitCheck.Core's own copy (netstandard2.0 has the same gap). Each
// assembly needs its own internal copy; a shared public one isn't worth the
// coupling for a compile-time-only marker with no runtime behavior.
internal static class IsExternalInit { }
