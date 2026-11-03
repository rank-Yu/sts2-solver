// Test-build startup check: force the JIT to compile every non-generic method in the Mod and
// Core assemblies. A method that the JIT rejects (InvalidProgramException) fails the check even
// when no scenario happens to execute that path. Generic methods are skipped because an open
// generic context cannot be prepared without its type arguments.
using System.Reflection;
using System.Runtime.CompilerServices;
using Sts2Solver.Simulation.Combat;

namespace Sts2Solver;

internal static class JitChecks
{
    internal static void Run()
    {
        List<Assembly> assemblies = [];
        Assembly modAssembly = typeof(ModEntry).Assembly;
        Assembly coreAssembly = typeof(Battle).Assembly;
        foreach (Assembly assembly in new[] { modAssembly, coreAssembly })
        {
            if (!assemblies.Contains(assembly))
                assemblies.Add(assembly);
        }

        int types = 0;
        int prepared = 0;
        int skippedGeneric = 0;
        int skippedUnsupported = 0;
        int asyncMoveNext = 0;
        List<string> failures = [];
        foreach (Assembly assembly in assemblies)
        {
            foreach (Type type in LoadableTypes(assembly))
            {
                types++;
                if (type.IsGenericTypeDefinition)
                {
                    skippedGeneric += CountGenericMethods(type);
                    continue;
                }
                foreach (MethodBase method in Methods(type))
                {
                    if (method.IsAbstract || method.ContainsGenericParameters || method.IsGenericMethod)
                    {
                        skippedGeneric++;
                        continue;
                    }
                    if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0
                        || (method.MethodImplementationFlags & MethodImplAttributes.InternalCall) != 0
                        || (method.CallingConvention & CallingConventions.VarArgs) != 0)
                    {
                        skippedUnsupported++;
                        continue;
                    }
                    if (method.Name == "MoveNext"
                        && typeof(IAsyncStateMachine).IsAssignableFrom(type))
                    {
                        asyncMoveNext++;
                    }
                    string name = $"{type.FullName}.{method.Name}";
                    try
                    {
                        RuntimeHelpers.PrepareMethod(method.MethodHandle);
                        prepared++;
                    }
                    catch (InvalidProgramException exception)
                    {
                        failures.Add($"{name}:InvalidProgram:{exception.Message}");
                    }
                    catch (TypeLoadException exception)
                    {
                        failures.Add($"{name}:TypeLoad:{exception.Message}");
                    }
                    catch (ArgumentException)
                    {
                        skippedUnsupported++;
                    }
                    catch (NotSupportedException)
                    {
                        skippedUnsupported++;
                    }
                    catch (InvalidOperationException)
                    {
                        skippedUnsupported++;
                    }
                }
            }
        }

        ModLog.Info(
            $"[Sts2Solver] JIT_PREPARE assemblies={assemblies.Count} types={types} " +
            $"prepared={prepared} async_move_next={asyncMoveNext} " +
            $"skipped_generic={skippedGeneric} skipped_unsupported={skippedUnsupported} " +
            $"failed={failures.Count}");
        foreach (string failure in failures)
            ModLog.Error($"[Sts2Solver] JIT_PREPARE_FAILED method={failure}");
        if (failures.Count > 0)
            throw new InvalidDataException($"jit_prepare failed={failures.Count}: " + string.Join("; ", failures));
    }

    private static IEnumerable<MethodBase> Methods(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (ConstructorInfo constructor in type.GetConstructors(flags))
            yield return constructor;
        if (type.TypeInitializer is { } initializer)
            yield return initializer;
        foreach (MethodInfo method in type.GetMethods(flags))
            yield return method;
    }

    private static int CountGenericMethods(Type type)
        => Methods(type).Count(static method => method.IsGenericMethod || method.ContainsGenericParameters);

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }
}
