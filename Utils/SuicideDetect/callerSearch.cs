using System;
using System.Collections.Generic;
using System.Reflection;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// Finds the methods that call a given method by reading IL, rather than by listing names. A
/// search rather than a list because the self-branches live in thirteen-plus differently named
/// methods, and because the suicide counter is also reached from types no list here mentions.
/// The scan is a raw byte search for a call opcode followed by the callee's metadata token, not
/// a real IL walk, so an operand can masquerade as an opcode. A false positive costs one extra
/// patched method whose instance is pushed, popped and then fails to resolve a name.
/// </summary>
internal static class CallerSearch
{
    internal const BindingFlags Declared =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    const byte Call = 0x28;
    const byte CallVirt = 0x6F;

    internal static List<MethodBase> Of(ICollection<MethodBase> callees, string description)
    {
        List<MethodBase> callers = new List<MethodBase>();
        if (callees == null || callees.Count == 0) return callers;

        HashSet<int> tokens = new HashSet<int>();
        HashSet<Type> owners = new HashSet<Type>();
        foreach (MethodBase callee in callees)
        {
            tokens.Add(callee.MetadataToken);
            if (callee.DeclaringType != null) owners.Add(callee.DeclaringType);
        }

        foreach (Type type in GameTypes())
        {
            try
            {
                // A callee's own class calls itself internally, and a generic definition has no
                // callable method to patch.
                if (owners.Contains(type) || type.ContainsGenericParameters) continue;

                foreach (MethodInfo method in type.GetMethods(Declared))
                {
                    // A static method has no instance to record, which is the only thing these
                    // scopes exist to capture.
                    if (method.IsStatic || method.IsAbstract || method.ContainsGenericParameters) continue;
                    if (Calls(method, tokens)) callers.Add(method);
                }
            }
            catch (Exception error)
            {
                Plugin.BepinLogger.LogError($"[SuicideDetect] Could not scan {type.FullName} for callers: {error}");
            }
        }

        Plugin.BepinLogger.LogInfo($"[SuicideDetect] Found {callers.Count} caller(s) of {description}.");
        return callers;
    }

    // A half-loadable assembly still yields the types that did load, and the emitters are plain
    // MonoBehaviours that will be among them.
    static IEnumerable<Type> GameTypes()
    {
        try
        {
            return typeof(Settings).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error)
        {
            Plugin.BepinLogger.LogWarning($"[SuicideDetect] Some game types failed to load while scanning: {error.Message}");
            return Array.FindAll(error.Types, type => type != null);
        }
    }

    static bool Calls(MethodInfo method, HashSet<int> tokens)
    {
        byte[] instructions;
        try
        {
            instructions = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch
        {
            // Abstract, extern and runtime-provided methods have no body to read.
            return false;
        }

        if (instructions == null) return false;

        for (int index = 0; index + 4 < instructions.Length; index++)
        {
            if (instructions[index] != Call && instructions[index] != CallVirt) continue;
            if (tokens.Contains(BitConverter.ToInt32(instructions, index + 1))) return true;
        }

        return false;
    }
}
