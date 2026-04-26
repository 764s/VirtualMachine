// Compatibility shim: UnscopedRefAttribute was introduced in .NET 7 / C# 11.
// Unity's Assembly-CSharp target (net471 + NET_STANDARD_2_1) does not include it.
// This definition is conditionally compiled so it is a no-op on net7+ targets
// where the real type already exists in System.Diagnostics.CodeAnalysis.
#if !NET7_0_OR_GREATER
namespace System.Diagnostics.CodeAnalysis
{
    [System.AttributeUsage(
        System.AttributeTargets.Method |
        System.AttributeTargets.Property |
        System.AttributeTargets.Parameter,
        Inherited = false)]
    internal sealed class UnscopedRefAttribute : System.Attribute
    {
    }
}
#endif
