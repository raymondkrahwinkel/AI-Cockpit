using System.Runtime.InteropServices;

namespace Cockpit.Infrastructure.Configuration;

// Sets or removes a variable in this process's environment in both places a value must land: .NET's managed
// copy and the native (libc) environment. On Unix, `Environment.SetEnvironmentVariable(string, string?)`
// only updates the managed copy — native libraries (Skia) and pty spawns read the native environ via
// `getenv`, so without `setenv`/`unsetenv` the change never reaches them or the children they
// start. Extracted from `Program`'s private helpers so startup repairs outside the App project follow the
// same rule.
//
// Call this during single-threaded startup only: glibc's `setenv` is not safe against a concurrent
// `getenv` from another thread (the environ array can be reallocated under the reader). Every current
// caller runs from `Program.Main` before hosted services, plugins or sessions exist — keep it that way.
public static class ProcessEnvironment
{
    [DllImport("libc", SetLastError = true)]
    private static extern int unsetenv(string name);

    [DllImport("libc", SetLastError = true)]
    private static extern int setenv(string name, string value, int overwrite);

    public static void Assign(string key, string value)
    {
        Environment.SetEnvironmentVariable(key, value);
        if (!OperatingSystem.IsWindows())
        {
            setenv(key, value, 1);
        }
    }

    public static void Remove(string key)
    {
        Environment.SetEnvironmentVariable(key, null);
        if (!OperatingSystem.IsWindows())
        {
            unsetenv(key);
        }
    }
}
