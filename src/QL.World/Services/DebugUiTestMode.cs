namespace QL.World.Services;

internal static class DebugUiTestMode
{
    private const string PRODUCTION_MUTEX_NAME =
        "Global\\QL_World_SingleInstance";
    private const string UI_TEST_MUTEX_NAME =
        "Local\\QL_World_UiTest_SingleInstance";
    private const string PRODUCTION_SHUTDOWN_EVENT_NAME =
        "Local\\QL_World_GracefulShutdown";
    private const string UI_TEST_SHUTDOWN_EVENT_NAME =
        "Local\\QL_World_UiTest_GracefulShutdown";
    internal const string EnvironmentVariable = "QL_WORLD_UI_TEST_MODE";

    internal static bool IsWindowTargetingEnabledDefault() =>
        IsWindowTargetingEnabled(
            Environment.GetEnvironmentVariable(EnvironmentVariable));

    internal static bool IsWindowTargetingEnabled(string? value)
    {
#if DEBUG
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
#else
        return false;
#endif
    }

    internal static string GetSingleInstanceMutexName(bool uiTestMode) =>
        uiTestMode ? UI_TEST_MUTEX_NAME : PRODUCTION_MUTEX_NAME;

    internal static string GetGracefulShutdownEventName(bool uiTestMode) =>
        uiTestMode
            ? UI_TEST_SHUTDOWN_EVENT_NAME
            : PRODUCTION_SHUTDOWN_EVENT_NAME;
}
