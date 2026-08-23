using System.Runtime.InteropServices;

namespace Cockpit.Infrastructure.Configuration;

// Sets/removes a variable in both the managed and native (libc) environment — on Unix, `Environment
// .SetEnvironmentVariable` alone leaves native libs and pty spawns reading a stale `getenv`. Call only
// during single-threaded startup: glibc's `setenv` races a concurrent `getenv` from another thread.
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
