using System.Reflection;
using System.Runtime.CompilerServices;

namespace MonoGame.AgentTools.Runtime;

/// <summary>
/// JIT-compiles the tools' own per-frame code up front, so the measuring code never shows up as a
/// first-use cost in the numbers it measures. Only for non-generic code (generic instantiations aren't
/// covered, which is why games should prewarm by running their real paths instead).
/// </summary>
internal static class Precompile
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    public static void Types(params Type[] types)
    {
        foreach (var type in types)
        {
            foreach (var method in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
            {
                if (!method.IsAbstract && !method.ContainsGenericParameters && method.GetMethodBody() is not null)
                    RuntimeHelpers.PrepareMethod(method.MethodHandle);
            }
        }
    }
}
