using System.Runtime.CompilerServices;

namespace Fkh.Backend.UnitTests;

internal static class TestBootstrap
{
    // FunctionBase's static initializer throws if ALLOWED_ORG_TEAMS is missing; set a
    // valid default before the type is first touched by any test. COMMON_CONTAINERS is
    // seeded here because it is read once into a static field at type load.
    [ModuleInitializer]
    internal static void Init()
    {
        Environment.SetEnvironmentVariable("ALLOWED_ORG_TEAMS", "[]");
        Environment.SetEnvironmentVariable("COMMON_CONTAINERS", "[\"shared-*\",\"demo\"]");
    }
}
