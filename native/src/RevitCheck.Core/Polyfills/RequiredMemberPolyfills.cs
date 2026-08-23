namespace System.Runtime.CompilerServices;

// netstandard2.0 predates C#'s `required` members too - same situation as
// IsExternalInit.cs, same standard compile-time-only polyfill pattern.

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
internal sealed class RequiredMemberAttribute : Attribute { }

[AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
internal sealed class CompilerFeatureRequiredAttribute : Attribute
{
    public CompilerFeatureRequiredAttribute(string featureName) => FeatureName = featureName;
    public string FeatureName { get; }
}
