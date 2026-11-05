// Starts one of the LivingNPC editors from the portable package: the bundled Node.js runs the editor's server.mjs and
// the browser opens on its page. Closing this console window stops the editor.
//
// Built twice by tools/build-editors.ps1 (csc /define:STUDIO or /define:MAPEDITOR). Written for the C# 5 compiler that
// ships with Windows (.NET Framework 4), so it needs nothing else installed.
//
// Folders, next to the exe:   runtime/node.exe   studio/ or map-editor/
// The worlds: env LIVINGNPC_WORLDS, else the first "worlds" folder found next to the exe or in up to three folders
// above it (the package can live inside the repository), else a new "worlds" folder next to the exe.

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;

static class Launcher
{
#if STUDIO
    const string Title = "LivingNPC Studio";
    const string AppFolder = "studio";
    const int Port = 5180;
#elif MAPEDITOR
    const string Title = "LivingNPC Map Editor";
    const string AppFolder = "map-editor";
    const int Port = 5173;
#else
#error Define STUDIO or MAPEDITOR
#endif

    static int Main(string[] args)
    {
        string url = "http://127.0.0.1:" + Port + "/";
        bool openBrowser = Array.IndexOf(args, "--no-browser") < 0;
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        string node = Path.Combine(exeDir, Path.Combine("runtime", "node.exe"));
        string app = Path.Combine(exeDir, AppFolder);
        Console.Title = Title + " - close this window to stop";

        if (!File.Exists(node) || !File.Exists(Path.Combine(app, "server.mjs")))
            return Fail("The package is incomplete: " + node + " or " + Path.Combine(app, "server.mjs") + " is missing.");

        if (PortOpen(Port))
        {
            Console.WriteLine(Title + " is already running at " + url);
            if (openBrowser) Open(url);
            Thread.Sleep(2000);
            return 0;
        }

        string worlds = FindWorlds(exeDir);
        Console.WriteLine(Title);
        Console.WriteLine("Worlds folder: " + worlds);
        Console.WriteLine("Page: " + url);
        Console.WriteLine("Close this window to stop the editor.");
        Console.WriteLine();

        var psi = new ProcessStartInfo(node, "server.mjs");
        psi.WorkingDirectory = app;
        psi.UseShellExecute = false;
        psi.EnvironmentVariables["LIVINGNPC_WORLDS"] = worlds;
        psi.EnvironmentVariables["PORT"] = Port.ToString();

        Process server;
        try { server = Process.Start(psi); }
        catch (Exception e) { return Fail("Node.js could not start: " + e.Message); }

        if (openBrowser)
        {
            for (int i = 0; i < 150 && !server.HasExited; i++)
            {
                if (PortOpen(Port)) { Open(url); break; }
                Thread.Sleep(100);
            }
        }
        server.WaitForExit();
        if (server.ExitCode != 0) return Fail("The editor stopped with an error (code " + server.ExitCode + ").");
        return 0;
    }

    static string FindWorlds(string exeDir)
    {
        string env = Environment.GetEnvironmentVariable("LIVINGNPC_WORLDS");
        if (!string.IsNullOrEmpty(env)) return Path.GetFullPath(env);
        var dir = new DirectoryInfo(exeDir);
        for (int i = 0; i < 4 && dir != null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "worlds");
            if (Directory.Exists(candidate)) return candidate;
        }
        string created = Path.Combine(exeDir, "worlds");
        Directory.CreateDirectory(created);
        return created;
    }

    static bool PortOpen(int port)
    {
        try
        {
            using (var client = new TcpClient())
            {
                var result = client.BeginConnect("127.0.0.1", port, null, null);
                bool ok = result.AsyncWaitHandle.WaitOne(200) && client.Connected;
                if (ok) client.EndConnect(result);
                return ok;
            }
        }
        catch { return false; }
    }

    static void Open(string url)
    {
        try { Process.Start(url); }
        catch (Exception e) { Console.WriteLine("Open " + url + " in your browser (" + e.Message + ")."); }
    }

    static int Fail(string message)
    {
        Console.WriteLine();
        Console.WriteLine(message);
        Console.WriteLine("Press a key to close.");
        try { Console.ReadKey(true); } catch { }
        return 1;
    }
}
