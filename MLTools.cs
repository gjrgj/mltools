using UnityEditor;
using UnityEngine;
using System.IO;
using System.Linq;

// This class extends the Unity Editor to automate a lot of the parts of the Magic Leap build process, along with providing useful ways to test and debug in-editor. 
// Created by George Hito.
public class MLTools : EditorWindow
{
    // checks for sdk, license, device, and if editor supports lumin
    bool supportsLumin = false;
    bool isSDKSet = false;
    bool isLicenseSet = false;
    bool isLumin = false;

    // relevant filepaths/messages
    string licenseName = "";
    string licenseFullPath = "";
    string sdkPath = "";
    string notification = "";
    string customCommand = "";
    string packageName = "";
    string appPath = "";

    // console window
    Object source;
    Vector2 scroll;
    string terminalOutput = "";
    string log = "";
    string prevMessage = "";
    int currentLogCount = 0;

    // log settings
    bool isContinuous = false;
    int logOptionIndex = 0;
    string[] logOptions = { "none", "brief", "color", "epoch", "long", "printable", "process", "raw" };
    int logLengthIndex = 0;
    string[] logLength = { "100 lines", "1000 lines", "10000 lines", "all" };

    // file management
    string fileToUploadPath = "";
    string fileToUploadName = "";
    bool isFileChosen = false;
    bool isFile = false;

    // build settings
    bool devBuild = false;

    // set button and labels layout
    GUILayoutOption[] buttons = { GUILayout.Width(300), GUILayout.ExpandWidth(true), GUILayout.MinWidth(100) };
    GUILayoutOption[] labels = { GUILayout.ExpandWidth(false) };

    // currently, a new process is spawned every time a command is sent. in the future need to figure out how to manage everything in a single process
    System.Diagnostics.Process p = null;
    System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();
    bool hasExited = true;


    // add MLTools under Magic Leap in the top menu
    [MenuItem("Magic Leap/Build Tools")]
    public static void ShowWindow()
    {
        //Show existing window instance. If one doesn't exist, make one.
        GetWindow(typeof(MLTools), false, "ML Tools", true);
    }

    // refresh checks whenever inspector updates
    void OnInspectorUpdate()
    {
        if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.Lumin)
        {
            if (EditorUserBuildSettings.GetPlatformSettings("Lumin", "SDKPath") != "")
            {
                isSDKSet = true;
                sdkPath = EditorUserBuildSettings.GetPlatformSettings("Lumin", "SDKPath");
                if (PlayerSettings.Lumin.CertificatePath != "")
                {
                    isLicenseSet = true;
                    licenseFullPath = PlayerSettings.Lumin.CertificatePath;
                }
                else
                {
                    isLicenseSet = false;
                }
            }
            else
            {
                isSDKSet = false;
            }
            isLumin = true;
        }
        else
        {
            isLumin = false;
        }
    }

    // on awake, check if a build folder exists, if not then create it
    void Awake()
    {
        // first check to see if Lumin is supported in the editor, if not disable window and notify user
        if (BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Lumin, BuildTarget.Lumin))
        {
            supportsLumin = true;
        }
        // generate build and logs directory if they don't exist, then check number of logs present so we can add new ones without overwriting
        if (!Directory.Exists(Application.dataPath + "/../Build/")){
            Directory.CreateDirectory(Application.dataPath + "/../Build/");
        }
        if (!Directory.Exists(Application.dataPath + "/../Logs/"))
        {
            Directory.CreateDirectory(Application.dataPath + "/../Logs/");
        }
       
        // set app datapath
        appPath = Application.dataPath;
    }

    void OnGUI()
    {
        // disable everything if editor doesn't support lumin and notify user
        using (new EditorGUI.DisabledScope(!supportsLumin))
        {
            // disable controls when a process is running
            using (new EditorGUI.DisabledScope(!hasExited))
            {
                setupEnv();
                manageBuilds();
                deviceControl();
                fileManagement();
                setupTerminalWindow();
            }
            // however, supply a force quit button to kill the process in case it hangs for too long
            if (hasExited == false && p != null)
            {
                p.Refresh();
                if ((System.DateTime.Now - p.StartTime).Seconds > 5)
                    forceQuit();
            }
        }
        if (!supportsLumin)
        {
            ShowNotification(new GUIContent("Lumin is not supported in this version of Unity. Please download a supported build from https://unity3d.com/partners/magicleap."));
        }
    }

    // start process, execute mldb command, return terminal output
    void ExecuteMLDBCommand(string command, string args)
    {
        // display command to user
        hasExited = false;
        p = new System.Diagnostics.Process();
        p.StartInfo.WorkingDirectory = sdkPath + "/tools/mldb/";
        p.StartInfo.FileName = "mldb.exe";
        // make sure user can't initiate an infinite log, and reset output whenever log is called so we can show the whole 100 lines
        if (command == "log")
        {
            terminalOutput = "";
            if (args == "")
            {
                args = "-d -t 100";
                terminalOutput += "No support for continuous logging yet. Default log gives 100 lines.";
            }
        }
        p.StartInfo.Arguments = command + " " + args;
        p.StartInfo.UseShellExecute = false;
        p.StartInfo.CreateNoWindow = true;
        p.StartInfo.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
        p.StartInfo.RedirectStandardOutput = true;
        p.Exited += new System.EventHandler(handleExit);
        p.EnableRaisingEvents = true;
        p.OutputDataReceived += new System.Diagnostics.DataReceivedEventHandler(
           (s, e) =>
           {
               string output = e.Data;
               prevMessage = output;
               switch (command)
               {
                   case "log":
                       terminalOutput += output;
                       terminalOutput += "\n";
                       log += output + "\n";
                       break;
                   default:
                       terminalOutput += output;
                       terminalOutput += "\n";
                       break;
               }
               // set scrollbar position to bottom when new data is received
               scroll.y = 10000;

               // if terminal output is too long, empty it 
               if (terminalOutput.Length >= 16382)
               {
                   terminalOutput = "";
               }
           }
       );
        // handle long commands like log and install individually, do not wait for exit here (thus not blocking the main threa
        terminalOutput += "mldb " + command + " " + args + "\n";
        p.Start();

        p.BeginOutputReadLine();
    }

    // when the process exits, close it 
    private void handleExit(object sender, System.EventArgs e)
    {
        // if we are logging, print location of log
        if (p.StartInfo.Arguments.Split(' ')[0] == "log")
        {
            terminalOutput += "Log saved to " + appPath + "/../Build/log_" + currentLogCount + ".txt\n";
        }
        p.Close();
        hasExited = true;
    }

    // use location of ML SDK to find envsetup.bat, everything else depends on this being set first
    void setupEnv()
    {
        // check environment settings, ensure everything is correct
        GUILayout.Label("Setup Environment", EditorStyles.boldLabel);
        if (!isLumin)
        {
            GUILayout.Label("Build target is not Lumin. Please update the target in Build Settings.", EditorStyles.helpBox);
        }
        if (!isLicenseSet)
        {
            GUILayout.Label("Certificate not set. Please update its location in Project Settings/Player.", EditorStyles.helpBox);
        }
        if (!isSDKSet)
        {
            GUILayout.Label("SDK not set. Please update its location in Build Settings.", EditorStyles.helpBox);
        }

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Current Build Target: ", labels);
        if (isLumin)
        {
            GUILayout.Label(EditorUserBuildSettings.activeBuildTarget.ToString(), labels);
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Certificate Path: ", labels);
        if (isLicenseSet)
        { 
            GUILayout.Label(licenseFullPath, labels);
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("SDK Path: ", labels);
        if (isSDKSet)
        {
            GUILayout.Label(sdkPath, labels);
        }    
        EditorGUILayout.EndHorizontal();      
    }

    // manage application building to device, checks if a 'Build' directory exists and if not then generates it
    void manageBuilds()
    {
        // grey out build commands if license not yet set, or sdk not set, or device not detected
        using (new EditorGUI.DisabledScope(isLicenseSet == false && isSDKSet == false))
        {
            GUILayout.Label("Build Tools", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Package Name", labels);
            packageName = GUILayout.TextField(packageName, 50);
            devBuild = EditorGUILayout.Toggle("Development Build?", devBuild);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Build Project", buttons))
            {
                // use Unity's build system and build a package to the Build folder
                // note that we use the default active scenes in build settings, can be changed by user manually
                // first get active scenes, then setup build pipeline, then build player to /Build/ directory
                EditorUserBuildSettings.SetPlatformSettings("Lumin", "SignMabuPackage", "true");
                BuildPlayerOptions bpo = new BuildPlayerOptions();
                bpo.scenes = (from scene in EditorBuildSettings.scenes where scene.enabled select scene.path).ToArray();
                bpo.locationPathName = Application.dataPath + "/../Build/" + packageName + ".mpk";
                bpo.target = BuildTarget.Lumin;
                if (devBuild)
                    bpo.options = BuildOptions.Development;
                else
                    bpo.options = BuildOptions.None;

                BuildPipeline.BuildPlayer(bpo);
            }

            if (GUILayout.Button("Install to Device", buttons))
            {
                // look for .mpk in build directory then send it to device, overwrite existing package
                ExecuteMLDBCommand("install", "-u " + Application.dataPath + "/../Build/" + packageName + ".mpk");
            }

            if (GUILayout.Button("Uninstall from Device", buttons))
            {
                ExecuteMLDBCommand("uninstall", PlayerSettings.applicationIdentifier);
            }
            EditorGUILayout.EndHorizontal();
        }
    }

    // start/stop applications remotely and dump device logs/bugreport
    void deviceControl()
    {
        GUILayout.Label("Device Control", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Launch on Device", buttons))
        {
            ExecuteMLDBCommand("launch", "-f " + PlayerSettings.applicationIdentifier);
        }
        if (GUILayout.Button("Force Quit on Device", buttons))
        {
            ExecuteMLDBCommand("terminate", "-f " + PlayerSettings.applicationIdentifier);
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Dump Device Log", buttons))
        {
            HandleLog();
            log = "";
        }
        logLengthIndex = EditorGUILayout.Popup(logLengthIndex, logLength);
        logOptionIndex = EditorGUILayout.Popup(logOptionIndex, logOptions);
        EditorGUILayout.EndHorizontal();
        if (GUILayout.Button("Generate Bug Report", buttons))
        {
            ExecuteMLDBCommand("bugreport", Application.dataPath + "/../Logs/" + packageName + "_bugreport.zip");
        }
    }

    // manages file upload/
    void fileManagement()
    {
        GUILayout.Label("App File Management", EditorStyles.boldLabel);
        // disable unless file is chosen
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Choose File", buttons))
        {
            isFile = true;
            fileToUploadPath = EditorUtility.OpenFilePanel("Choose file to upload to device", "", "");
            // parse filename
            string temp = System.String.Copy(fileToUploadPath);
            string[] filePath = temp.Split('/');
            fileToUploadName = System.String.Copy(filePath[filePath.Length - 1]);
        }
        if (GUILayout.Button("Choose Folder", buttons))
        {
            isFile = false;
            fileToUploadPath = EditorUtility.OpenFolderPanel("Choose folder to upload to device", "", "");
            // parse folder name
            string temp = System.String.Copy(fileToUploadPath);
            string[] filePath = temp.Split('/');
            fileToUploadName = System.String.Copy(filePath[filePath.Length - 1]);
        }
        GUILayout.Label(fileToUploadPath, labels);
        EditorGUILayout.EndHorizontal();
        using (new EditorGUI.DisabledScope(fileToUploadPath == ""))
        {
            EditorGUILayout.BeginHorizontal();
            if (isFile)
            {
                if (GUILayout.Button("Send File to Device", buttons))
                {
                    ExecuteMLDBCommand("push", "-p " + PlayerSettings.applicationIdentifier + " -v " + fileToUploadPath + " /documents/C2/" + fileToUploadName);
                }
            }
            else
            {
                if (GUILayout.Button("Send Folder to Device", buttons))
                {
                    ExecuteMLDBCommand("push", "-p " + PlayerSettings.applicationIdentifier + " -v " + fileToUploadPath + " /documents/C2/" + fileToUploadName);
                }
            }
        }
        if (GUILayout.Button("List Files", buttons))
        {
            ExecuteMLDBCommand("ls", "-p " + PlayerSettings.applicationIdentifier + " -l /documents/C2/");
        }
        EditorGUILayout.EndHorizontal();
    }

    // setup terminal display
    void setupTerminalWindow() {
        using (var scrollViewScope = new EditorGUILayout.ScrollViewScope(scroll))
        {
            scroll = scrollViewScope.scrollPosition;
            GUILayout.Label(terminalOutput);
        }
        // allow the user to input custom commands
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Send Custom Command", labels);
        if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return)
        {
            ExecuteMLDBCommand(customCommand, "");
        }
        customCommand = GUILayout.TextField(customCommand, 100, GUILayout.ExpandWidth(true), GUILayout.Width(200));
        if (GUILayout.Button("List Commands", buttons))
        {
            terminalOutput = "";
            ExecuteMLDBCommand("help", "");
        }
        EditorGUILayout.EndHorizontal();
        if (GUILayout.Button("Clear Output", buttons))
        {
            terminalOutput = "";
        }
    }

    // handles possible log options
    void HandleLog()
    {
        // check current number of logs
        currentLogCount = Directory.GetFiles(Application.dataPath + "/../Logs/").Length;
        // display different log based on options. never display continuous log though
        // dumps entire log to file as we have saved everything from this session
        string ll = "";
        switch(logLengthIndex)
        {
            case 0:
                ll += " -t 100";
                break;
            case 1:
                ll += " -t 1000";
                break;
            case 2:
                ll += " -t 100000";
                break;
            case 3:
                break;
        }
        switch (logOptionIndex)
        {
            case 0:
                ExecuteMLDBCommand("log", "-d" + ll);
                break;
            case 1:
                ExecuteMLDBCommand("log", "-d -v brief" + ll);
                break;
            case 2:
                ExecuteMLDBCommand("log", "-d -v color" + ll);
                break;
            case 3:
                ExecuteMLDBCommand("log", "-d -v epoch" + ll);
                break;
            case 4:
                ExecuteMLDBCommand("log", "-d -v long" + ll);
                break;
            case 5:
                ExecuteMLDBCommand("log", "-d -v printable" + ll);
                break;
            case 6:
                ExecuteMLDBCommand("log", "-d -v process" + ll);
                break;
            case 7:
                ExecuteMLDBCommand("log", "-d -v raw" + ll);
                break;
        }

        // save log to file in build folder
        File.WriteAllText(Application.dataPath + "/../Logs/log_" + currentLogCount + ".txt", log);
    }

    // stops the currently running process if it hangs for too long
    void forceQuit()
    {
        if (GUILayout.Button("Force Quit Process", buttons))
            p.Kill();
    }
}