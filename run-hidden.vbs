Option Explicit

Dim shell
Dim runnerPath
Dim command

If WScript.Arguments.Count <> 1 Then
    WScript.Quit 2
End If

runnerPath = WScript.Arguments(0)
Set shell = CreateObject("WScript.Shell")
command = "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " & QuoteArgument(runnerPath)

WScript.Quit shell.Run(command, 0, False)

Function QuoteArgument(ByVal value)
    QuoteArgument = Chr(34) & Replace(value, Chr(34), Chr(34) & Chr(34)) & Chr(34)
End Function
