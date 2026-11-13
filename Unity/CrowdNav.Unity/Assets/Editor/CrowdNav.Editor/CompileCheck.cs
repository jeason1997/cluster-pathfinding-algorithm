using UnityEditor;
using UnityEngine;

public static class CompileCheck
{
    public static void Run()
    {
        Debug.Log("COMPILE_OK " + Application.unityVersion);
        EditorApplication.Exit(0);
    }
}