using PtySpike;

// SPIKE driver. Usage:
//   dotnet run -- echo     -> hosts `cmd.exe` and proves stdin injection + output readback works at all
//   dotnet run -- claude   -> hosts the real claude.exe in a ConPTY and does a minimal "say hallo" round trip
//   dotnet run -- claude "C:\path\to\claude.exe" "C:\working\dir"   -> override exe/workdir

string mode = args.Length > 0 ? args[0] : "echo";

if (mode == "sizecheck")
{
    PtySpike.SizeCheck.Run();
    return;
}

if (mode == "echo")
{
    RunEchoTest();
}
else if (mode == "onelineecho")
{
    RunOneLineEchoTest();
}
else if (mode == "sideeffect")
{
    RunSideEffectTest();
}
else if (mode == "claude")
{
    string exe = args.Length > 1 ? args[1] : @"C:\Users\raymo\AppData\Roaming\Claude\claude-code\2.1.197\claude.exe";
    string cwd = args.Length > 2 ? args[2] : Directory.GetCurrentDirectory();
    RunClaudeTest(exe, cwd);
}
else
{
    Console.WriteLine($"Unknown mode '{mode}'. Use 'echo' or 'claude'.");
}

return;

static void RunEchoTest()
{
    Console.WriteLine("=== ConPTY echo test: hosting cmd.exe ===");
    using var pty = ConPtyProcess.Start("cmd.exe", Directory.GetCurrentDirectory());
    Console.WriteLine($"Started cmd.exe under ConPTY, PID={pty.ProcessId}");

    var readTask = Task.Run(() => PumpOutputRaw(pty.OutputReader, "OUT"));

    Thread.Sleep(1500); // let the shell settle & print its banner/prompt

    Console.WriteLine("[TEST] Writing 'echo HELLO_FROM_INJECTED_STDIN' + CRLF to PTY input...");
    WriteToPty(pty, "echo HELLO_FROM_INJECTED_STDIN\r\n");

    Thread.Sleep(1500);

    Console.WriteLine("[TEST] Writing 'exit' + CRLF to end the shell...");
    WriteToPty(pty, "exit\r\n");

    Thread.Sleep(1500);
    Console.WriteLine("=== echo test done, waiting for output pump to drain ===");
    readTask.Wait(3000);
    Console.WriteLine("=== echo test fully done, see OUT lines above for round-trip proof ===");
}

static void RunSideEffectTest()
{
    // Independent of whether we can READ output via the pipe: does stdin
    // injection actually reach the hosted shell and get executed? Prove it
    // via an observable side effect on disk instead of relying on pipe
    // readback, so this test isolates the INPUT direction only.
    string marker = Path.Combine(Directory.GetCurrentDirectory(), "sideeffect-proof.txt");
    if (File.Exists(marker)) File.Delete(marker);

    Console.WriteLine("=== ConPTY stdin-injection side-effect test ===");
    using var pty = ConPtyProcess.Start("cmd.exe", Directory.GetCurrentDirectory());
    Console.WriteLine($"Started cmd.exe under ConPTY, PID={pty.ProcessId}");
    var readTask = Task.Run(() => PumpOutputRaw(pty.OutputReader, "SIDEEFFECT"));

    Thread.Sleep(1500);
    WriteToPty(pty, $"echo INJECTED > \"{marker}\"\r\n");
    Thread.Sleep(1500);
    WriteToPty(pty, "exit\r\n");
    Thread.Sleep(1500);

    if (File.Exists(marker))
    {
        Console.WriteLine($"[RESULT] PASS: marker file was created by injected command. Contents: {File.ReadAllText(marker).Trim()}");
    }
    else
    {
        Console.WriteLine("[RESULT] FAIL: marker file was NOT created - injected stdin did not reach/execute in the shell.");
    }
}

static void RunOneLineEchoTest()
{
    Console.WriteLine("=== ConPTY one-shot test: `cmd.exe /c echo PIPE_PROOF_12345` ===");
    using var pty = ConPtyProcess.Start("cmd.exe /c \"echo PIPE_PROOF_12345\"", Directory.GetCurrentDirectory());
    Console.WriteLine($"Started, PID={pty.ProcessId}");
    var readTask = Task.Run(() => PumpOutputRaw(pty.OutputReader, "ONESHOT"));
    readTask.Wait(5000);
    Console.WriteLine("=== one-shot test done ===");
}

static void RunClaudeTest(string exePath, string workDir)
{
    if (!File.Exists(exePath))
    {
        Console.WriteLine($"claude.exe not found at {exePath}");
        return;
    }

    Console.WriteLine("=== ConPTY claude test: hosting real claude.exe interactively ===");
    Console.WriteLine($"exe={exePath}");
    Console.WriteLine($"cwd={workDir}");

    // Quote the exe path since it contains spaces (Program Files-like AppData path segments).
    string commandLine = $"\"{exePath}\"";

    using var pty = ConPtyProcess.Start(commandLine, workDir);
    Console.WriteLine($"Started claude.exe under ConPTY, PID={pty.ProcessId}");

    var readTask = Task.Run(() => PumpOutputRaw(pty.OutputReader, "CLAUDE-OUT"));

    Console.WriteLine("[TEST] Waiting 4s for TUI to render / auth check...");
    Thread.Sleep(4000);

    Console.WriteLine("[TEST] Injecting 'zeg alleen het woord hallo' + Enter...");
    WriteToPty(pty, "zeg alleen het woord hallo\r");

    Thread.Sleep(8000); // give the model time to respond

    Console.WriteLine("[TEST] Injecting '/exit' + Enter to end session cleanly...");
    WriteToPty(pty, "/exit\r");

    Thread.Sleep(2000);
    Console.WriteLine("=== claude test done, inspect CLAUDE-OUT lines above ===");
}

static void WriteToPty(ConPtyProcess pty, string text)
{
    var bytes = System.Text.Encoding.UTF8.GetBytes(text);
    pty.InputWriter.Write(bytes, 0, bytes.Length);
    pty.InputWriter.Flush();
}

static void PumpOutputRaw(FileStream stream, string tag)
{
    var buffer = new byte[4096];
    string logPath = Path.Combine(Directory.GetCurrentDirectory(), $"pty-capture-{tag}.log");
    using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
    try
    {
        while (true)
        {
            int n = stream.Read(buffer, 0, buffer.Length);
            if (n <= 0) break;
            string chunk = System.Text.Encoding.UTF8.GetString(buffer, 0, n);
            string line = $"[{DateTime.Now:HH:mm:ss.fff} {tag} chunk, {n} bytes]: {EscapeForDisplay(chunk)}";
            Console.WriteLine(line);
            Console.Out.Flush();
            log.WriteLine(line);
        }
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff} {tag}] read loop ended (n<=0)");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff} {tag}] reader ended: {ex.Message}");
    }
}

static string EscapeForDisplay(string s)
{
    return s.Replace("\x1b", "\\e").Replace("\r", "\\r").Replace("\n", "\\n\n");
}
