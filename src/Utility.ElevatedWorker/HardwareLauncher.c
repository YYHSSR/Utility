#include <windows.h>
#include <strsafe.h>
#include <wchar.h>

// The scheduled launcher starts only the adjacent worker and supplies a fresh environment.
// The worker validates the pipe peer, request token, and allowed firmware operation.
int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR arguments, int show)
{
    (void)instance;
    (void)previous;
    (void)show;

    WCHAR directory[MAX_PATH];
    WCHAR worker[MAX_PATH];
    WCHAR command[2 * MAX_PATH];
    WCHAR windows[MAX_PATH];
    WCHAR system[MAX_PATH];
    DWORD length = GetModuleFileNameW(NULL, directory, ARRAYSIZE(directory));
    UINT windowsLength = GetWindowsDirectoryW(windows, ARRAYSIZE(windows));
    UINT systemLength = GetSystemDirectoryW(system, ARRAYSIZE(system));
    if (!length || length >= ARRAYSIZE(directory) || !windowsLength || windowsLength >= ARRAYSIZE(windows) ||
        !systemLength || systemLength >= ARRAYSIZE(system))
        return ERROR_BAD_PATHNAME;

    WCHAR *separator = wcsrchr(directory, L'\\');
    if (!separator)
        return ERROR_BAD_PATHNAME;
    *separator = L'\0';

    if (FAILED(StringCchPrintfW(worker, ARRAYSIZE(worker), L"%ls\\Utility.ElevatedWorker.exe", directory)) ||
        FAILED(StringCchPrintfW(command, ARRAYSIZE(command), L"\"%ls\" --hardware-task %ls", worker, arguments)))
        return ERROR_BAD_ARGUMENTS;

    // CreateProcess expects null-terminated NAME=value entries and an additional null at the end.
    WCHAR environment[4 * MAX_PATH] = {0};
    WCHAR *next = environment;
    size_t remaining = ARRAYSIZE(environment);
    if (FAILED(StringCchCopyExW(next, remaining, L"DOTNET_EnableDiagnostics=0", &next, &remaining, 0)) ||
        FAILED(StringCchPrintfExW(next + 1, remaining - 1, &next, &remaining, 0, L"Path=%ls;%ls", system, windows)) ||
        FAILED(StringCchPrintfExW(next + 1, remaining - 1, &next, &remaining, 0, L"SystemRoot=%ls", windows)) ||
        FAILED(StringCchPrintfExW(next + 1, remaining - 1, &next, &remaining, 0, L"windir=%ls", windows)) || remaining < 2)
        return ERROR_INSUFFICIENT_BUFFER;
    next[1] = L'\0';

    STARTUPINFOW startup = { .cb = sizeof(startup) };
    PROCESS_INFORMATION process = {0};
    if (!CreateProcessW(worker, command, NULL, NULL, FALSE, CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
                        environment, directory, &startup, &process))
        return (int)GetLastError();

    CloseHandle(process.hThread);
    DWORD result = ERROR_GEN_FAILURE;
    if (WaitForSingleObject(process.hProcess, INFINITE) == WAIT_OBJECT_0)
        GetExitCodeProcess(process.hProcess, &result);
    CloseHandle(process.hProcess);
    return (int)result;
}
