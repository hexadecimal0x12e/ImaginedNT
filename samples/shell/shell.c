// A minimal cmd.exe-style shell that runs inside ImaginedNT as a real guest PE.
//
// It is intentionally CRT-free (built with /NODEFAULTLIB and NtEntry), so every string,
// number-formatting and UTF-8/UTF-16 helper below is local.  The commands exercise the managed
// process, file, directory-enumeration and console APIs:
//
//   help | dir | cd/chdir | pwd | type | echo | mkdir/md | rmdir/rd | del/erase |
//   copy | move/ren | run/start | cls | exit
//
// Use it interactively:
//   ImaginedNT.Host run samples/bin/shell.exe --strict --sandbox <dir>
//
// or script it by redirecting stdin:
//   "dir`ntype file.txt`nexit" | ImaginedNT.Host run samples/bin/shell.exe --strict --sandbox <dir>

#include <windows.h>

#define SHELL_LINE_MAX  512
#define SHELL_ARG_MAX   16
#define SHELL_IO_BUF    1024
#define SHELL_COPY_BUF  8192
#define SHELL_WIDE_MAX  1024
#define SHELL_UTF8_MAX  2048

static HANDLE g_in;
static HANDLE g_out;

static char g_io[SHELL_IO_BUF];
static DWORD g_io_len;
static DWORD g_io_pos;

static char g_utf8[SHELL_UTF8_MAX];
static char g_command[SHELL_LINE_MAX];
static WCHAR g_wide[SHELL_WIDE_MAX];
static WCHAR g_command_line[SHELL_WIDE_MAX];
static char g_copy[SHELL_COPY_BUF];

// --- tiny CRT-free helpers -------------------------------------------------------------

static int StrLen(const char *text)
{
    const char *end = text;
    while (*end) end++;
    return (int)(end - text);
}

static void CopyString(const char *source, char *target, int capacity)
{
    int index = 0;
    if (capacity <= 0) return;
    while (source[index] && index < capacity - 1)
    {
        target[index] = source[index];
        index++;
    }
    target[index] = 0;
}

static int LowerAscii(int value)
{
    return (value >= 'A' && value <= 'Z') ? value + ('a' - 'A') : value;
}

static int StrEqI(const char *left, const char *right)
{
    while (*left && *right)
    {
        if (LowerAscii((unsigned char)*left) != LowerAscii((unsigned char)*right)) return 0;
        left++;
        right++;
    }
    return *left == *right;
}

static DWORD ParseUInt(const char *text)
{
    DWORD value = 0;
    while (*text == ' ' || *text == '\t') text++;
    while (*text >= '0' && *text <= '9')
    {
        value = value * 10 + (DWORD)(*text - '0');
        text++;
    }
    return value;
}

static void OutN(const char *text, int length)
{
    DWORD written = 0;
    if (length > 0) WriteFile(g_out, text, (DWORD)length, &written, 0);
}

static void Out(const char *text)
{
    OutN(text, StrLen(text));
}

static void OutLine(const char *text)
{
    Out(text);
    Out("\r\n");
}

static void OutUInt(DWORD value)
{
    char buffer[16];
    int count = 0;
    if (value == 0)
    {
        Out("0");
        return;
    }
    while (value != 0 && count < 16)
    {
        buffer[count++] = (char)('0' + (value % 10));
        value /= 10;
    }
    while (count > 0) OutN(&buffer[--count], 1);
}

static void OutSpaces(int count)
{
    while (count-- > 0) Out(" ");
}

static void PrintWinError(const char *prefix, DWORD error)
{
    Out(prefix);
    Out(": failed, error ");
    OutUInt(error);
    OutLine("");
}

static int WideLen(const WCHAR *text)
{
    const WCHAR *end = text;
    while (*end) end++;
    return (int)(end - text);
}

static int HasWildcard(const char *text)
{
    while (*text)
    {
        if (*text == '*' || *text == '?') return 1;
        text++;
    }
    return 0;
}

// UTF-8 -> UTF-16.  Invalid sequences become U+FFFD; the shell only needs to round-trip paths
// that the host produced, which are valid UTF-8.
static int Utf8ToWide(const char *source, WCHAR *target, int capacity)
{
    int written = 0;
    if (capacity <= 0) return 0;
    while (*source && written < capacity - 1)
    {
        unsigned int value = (unsigned char)*source++;
        if (value < 0x80)
        {
            target[written++] = (WCHAR)value;
            continue;
        }

        int continuation = 0;
        unsigned int minimum = 0;
        if ((value & 0xE0) == 0xC0) { continuation = 1; value &= 0x1F; minimum = 0x80; }
        else if ((value & 0xF0) == 0xE0) { continuation = 2; value &= 0x0F; minimum = 0x800; }
        else if ((value & 0xF8) == 0xF0) { continuation = 3; value &= 0x07; minimum = 0x10000; }
        else { target[written++] = 0xFFFD; continue; }

        int valid = 1;
        for (int i = 0; i < continuation; i++)
        {
            unsigned char next = (unsigned char)*source;
            if ((next & 0xC0) != 0x80) { valid = 0; break; }
            value = (value << 6) | (next & 0x3Fu);
            source++;
        }
        if (!valid || value < minimum)
        {
            target[written++] = 0xFFFD;
            continue;
        }

        if (value >= 0x10000)
        {
            if (written + 2 > capacity - 1) break;
            value -= 0x10000;
            target[written++] = (WCHAR)(0xD800 + (value >> 10));
            target[written++] = (WCHAR)(0xDC00 + (value & 0x3FF));
        }
        else
        {
            target[written++] = (WCHAR)value;
        }
    }
    target[written] = 0;
    return written;
}

// UTF-16 -> UTF-8.
static int WideToUtf8(const WCHAR *source, char *target, int capacity)
{
    int written = 0;
    if (capacity <= 0) return 0;
    while (*source && written < capacity - 4)
    {
        unsigned int value = *source++;
        if (value >= 0xD800 && value <= 0xDBFF && *source >= 0xDC00 && *source <= 0xDFFF)
        {
            unsigned int low = *source++;
            value = 0x10000 + ((value - 0xD800) << 10) + (low - 0xDC00);
        }

        if (value < 0x80)
        {
            target[written++] = (char)value;
        }
        else if (value < 0x800)
        {
            target[written++] = (char)(0xC0 | (value >> 6));
            target[written++] = (char)(0x80 | (value & 0x3F));
        }
        else if (value < 0x10000)
        {
            target[written++] = (char)(0xE0 | (value >> 12));
            target[written++] = (char)(0x80 | ((value >> 6) & 0x3F));
            target[written++] = (char)(0x80 | (value & 0x3F));
        }
        else
        {
            target[written++] = (char)(0xF0 | (value >> 18));
            target[written++] = (char)(0x80 | ((value >> 12) & 0x3F));
            target[written++] = (char)(0x80 | ((value >> 6) & 0x3F));
            target[written++] = (char)(0x80 | (value & 0x3F));
        }
    }
    target[written] = 0;
    return written;
}

// --- console input ---------------------------------------------------------------------

static int FillInput(void)
{
    DWORD read = 0;
    g_io_len = 0;
    g_io_pos = 0;
    if (!ReadFile(g_in, g_io, SHELL_IO_BUF, &read, 0))
        return -1;
    g_io_len = read;
    return read == 0 ? 0 : 1;
}

// Reads one line without the newline.  Returns -1 on end of input.
static int ReadLine(char *line, int capacity)
{
    int length = 0;
    int sawInput = 0;
    for (;;)
    {
        if (g_io_pos >= g_io_len)
        {
            int state = FillInput();
            if (state < 0) return -1;
            if (state == 0)
            {
                if (!sawInput) return -1;
                break;
            }
        }

        char character = g_io[g_io_pos++];
        sawInput = 1;
        if (character == '\r') continue;
        if (character == '\n') break;
        if (length < capacity - 1) line[length++] = character;
    }
    line[length] = 0;
    return length;
}

static void PrintCwd(void)
{
    WCHAR current[512];
    DWORD length = GetCurrentDirectoryW(512, current);
    if (length == 0 || length >= 512)
    {
        Out("C:\\");
        return;
    }

    if (current[0] == L'\\')
    {
        Out("C:");
        WideToUtf8(current, g_utf8, SHELL_UTF8_MAX);
        Out(g_utf8);
    }
    else
    {
        WideToUtf8(current, g_utf8, SHELL_UTF8_MAX);
        Out(g_utf8);
    }
}

static void PrintPrompt(void)
{
    PrintCwd();
    Out("> ");
}

// --- commands --------------------------------------------------------------------------

static void PrintSize(DWORD size)
{
    char digits[16];
    int count = 0;
    if (size == 0)
    {
        digits[count++] = '0';
    }
    else
    {
        while (size != 0 && count < 16)
        {
            digits[count++] = (char)('0' + (size % 10));
            size /= 10;
        }
    }
    OutSpaces(10 - count);
    while (count > 0) OutN(&digits[--count], 1);
}

static void CmdHelp(void)
{
    OutLine("ImaginedNT minimal shell commands:");
    OutLine("  help                     show this help");
    OutLine("  dir [path]               list a directory (wildcards allowed)");
    OutLine("  cd [path] / chdir        show or change the current directory");
    OutLine("  pwd                      print the current directory");
    OutLine("  type <file>              print a file");
    OutLine("  echo <text>              print text");
    OutLine("  mkdir <dir> / md         create a directory");
    OutLine("  rmdir <dir> / rd         remove an empty directory");
    OutLine("  del <file> / erase       delete a file");
    OutLine("  copy <src> <dst>         copy a file");
    OutLine("  move <src> <dst> / ren   rename or move a file");
    OutLine("  run <image> [args]       create a guest process and wait for it");
    OutLine("  cls                      clear the screen");
    OutLine("  exit [code]              leave the shell");
}

static void CmdEcho(int argc, char **argv)
{
    for (int i = 1; i < argc; i++)
    {
        if (i > 1) Out(" ");
        Out(argv[i]);
    }
    OutLine("");
}

static void CmdDir(int argc, char **argv)
{
    WCHAR pattern[MAX_PATH];
    if (argc > 1)
    {
        Utf8ToWide(argv[1], pattern, MAX_PATH);
        if (!HasWildcard(argv[1]))
        {
            DWORD attributes = GetFileAttributesW(pattern);
            if (attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
            {
                int length = WideLen(pattern);
                if (length > 0 && pattern[length - 1] != L'\\' && length < MAX_PATH - 2)
                    pattern[length++] = L'\\';
                if (length < MAX_PATH - 1)
                {
                    pattern[length++] = L'*';
                    pattern[length] = 0;
                }
            }
        }
    }
    else
    {
        Utf8ToWide("*", pattern, MAX_PATH);
    }

    Out(" Directory of ");
    PrintCwd();
    OutLine("");

    WIN32_FIND_DATAW data;
    HANDLE find = FindFirstFileW(pattern, &data);
    if (find == INVALID_HANDLE_VALUE)
    {
        DWORD error = GetLastError();
        if (error == ERROR_FILE_NOT_FOUND)
            OutLine("  File Not Found");
        else
            PrintWinError("dir", error);
        return;
    }

    DWORD count = 0;
    DWORD error = 0;
    for (;;)
    {
        int isDot = data.cFileName[0] == L'.' &&
                    (data.cFileName[1] == 0 || (data.cFileName[1] == L'.' && data.cFileName[2] == 0));
        if (!isDot)
        {
            if ((data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
            {
                Out("<DIR>          ");
            }
            else
            {
                PrintSize(data.nFileSizeLow);
                Out(" ");
            }
            WideToUtf8(data.cFileName, g_utf8, SHELL_UTF8_MAX);
            OutLine(g_utf8);
            count++;
        }

        if (!FindNextFileW(find, &data))
        {
            error = GetLastError();
            break;
        }
    }
    FindClose(find);

    Out("  ");
    OutUInt(count);
    OutLine(" file(s).");
    if (error != 0 && error != ERROR_NO_MORE_FILES)
        PrintWinError("dir", error);
}

static void CmdCd(int argc, char **argv)
{
    if (argc < 2)
    {
        PrintCwd();
        OutLine("");
        return;
    }

    Utf8ToWide(argv[1], g_wide, SHELL_WIDE_MAX);
    if (!SetCurrentDirectoryW(g_wide))
        PrintWinError("cd", GetLastError());
}

static void CmdPwd(void)
{
    PrintCwd();
    OutLine("");
}

static void CmdType(int argc, char **argv)
{
    if (argc < 2)
    {
        OutLine("type: file name required");
        return;
    }

    Utf8ToWide(argv[1], g_wide, SHELL_WIDE_MAX);
    HANDLE file = CreateFileW(g_wide, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, 0,
                              OPEN_EXISTING, 0, 0);
    if (file == INVALID_HANDLE_VALUE)
    {
        PrintWinError("type", GetLastError());
        return;
    }

    char buffer[1024];
    DWORD error = 0;
    for (;;)
    {
        DWORD read = 0;
        DWORD written = 0;
        if (!ReadFile(file, buffer, (DWORD)sizeof(buffer), &read, 0))
        {
            error = GetLastError();
            break;
        }
        if (read == 0) break;
        if (!WriteFile(g_out, buffer, read, &written, 0) || written != read)
        {
            error = GetLastError();
            break;
        }
    }
    CloseHandle(file);
    if (error != 0) PrintWinError("type", error);
}

static void CmdMkdir(int argc, char **argv)
{
    if (argc < 2)
    {
        OutLine("mkdir: directory name required");
        return;
    }
    Utf8ToWide(argv[1], g_wide, SHELL_WIDE_MAX);
    if (!CreateDirectoryW(g_wide, 0))
    {
        DWORD error = GetLastError();
        if (error != ERROR_ALREADY_EXISTS) PrintWinError("mkdir", error);
    }
}

static void CmdRmdir(int argc, char **argv)
{
    if (argc < 2)
    {
        OutLine("rmdir: directory name required");
        return;
    }
    Utf8ToWide(argv[1], g_wide, SHELL_WIDE_MAX);
    if (!RemoveDirectoryW(g_wide)) PrintWinError("rmdir", GetLastError());
}

static void CmdDel(int argc, char **argv)
{
    if (argc < 2)
    {
        OutLine("del: file name required");
        return;
    }
    Utf8ToWide(argv[1], g_wide, SHELL_WIDE_MAX);
    if (!DeleteFileW(g_wide)) PrintWinError("del", GetLastError());
}

static void CmdCopy(int argc, char **argv)
{
    if (argc < 3)
    {
        OutLine("copy: source and destination required");
        return;
    }

    WCHAR source[MAX_PATH];
    WCHAR target[MAX_PATH];
    Utf8ToWide(argv[1], source, MAX_PATH);
    Utf8ToWide(argv[2], target, MAX_PATH);

    HANDLE input = CreateFileW(source, GENERIC_READ, FILE_SHARE_READ, 0, OPEN_EXISTING, 0, 0);
    if (input == INVALID_HANDLE_VALUE)
    {
        PrintWinError("copy", GetLastError());
        return;
    }

    HANDLE output = CreateFileW(target, GENERIC_WRITE, 0, 0, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, 0);
    if (output == INVALID_HANDLE_VALUE)
    {
        DWORD error = GetLastError();
        CloseHandle(input);
        PrintWinError("copy", error);
        return;
    }

    DWORD total = 0;
    DWORD error = 0;
    for (;;)
    {
        DWORD read = 0;
        DWORD written = 0;
        if (!ReadFile(input, g_copy, SHELL_COPY_BUF, &read, 0))
        {
            error = GetLastError();
            break;
        }
        if (read == 0) break;
        if (!WriteFile(output, g_copy, read, &written, 0) || written != read)
        {
            error = GetLastError();
            break;
        }
        total += read;
    }

    CloseHandle(input);
    CloseHandle(output);
    if (error != 0)
    {
        PrintWinError("copy", error);
        return;
    }
    Out("  ");
    OutUInt(total);
    OutLine(" byte(s) copied.");
}

static void CmdMove(int argc, char **argv)
{
    if (argc < 3)
    {
        OutLine("move: source and destination required");
        return;
    }
    WCHAR source[MAX_PATH];
    WCHAR target[MAX_PATH];
    Utf8ToWide(argv[1], source, MAX_PATH);
    Utf8ToWide(argv[2], target, MAX_PATH);
    if (!MoveFileExW(source, target, MOVEFILE_REPLACE_EXISTING))
        PrintWinError("move", GetLastError());
}

static void CmdRun(int argc, char **argv)
{
    if (argc < 2)
    {
        OutLine("run: image name required");
        return;
    }

    int length = 0;
    for (int i = 1; i < argc && length < SHELL_WIDE_MAX - 1; i++)
    {
        int quote = 0;
        for (const char *p = argv[i]; *p; p++)
        {
            if (*p == ' ' || *p == '\t') { quote = 1; break; }
        }

        if (i > 1) g_command_line[length++] = L' ';
        if (quote && length < SHELL_WIDE_MAX - 2) g_command_line[length++] = L'"';

        length += Utf8ToWide(argv[i], g_command_line + length, SHELL_WIDE_MAX - length);

        if (quote && length < SHELL_WIDE_MAX - 1) g_command_line[length++] = L'"';
    }
    g_command_line[length] = 0;

    STARTUPINFOW startup;
    startup.cb = (DWORD)sizeof(startup);
    startup.lpReserved = 0;
    startup.lpDesktop = 0;
    startup.lpTitle = 0;
    startup.dwX = 0;
    startup.dwY = 0;
    startup.dwXSize = 0;
    startup.dwYSize = 0;
    startup.dwXCountChars = 0;
    startup.dwYCountChars = 0;
    startup.dwFillAttribute = 0;
    startup.dwFlags = 0;
    startup.wShowWindow = 0;
    startup.cbReserved2 = 0;
    startup.lpReserved2 = 0;
    startup.hStdInput = 0;
    startup.hStdOutput = 0;
    startup.hStdError = 0;

    PROCESS_INFORMATION child;
    child.hProcess = 0;
    child.hThread = 0;
    child.dwProcessId = 0;
    child.dwThreadId = 0;

    if (!CreateProcessW(0, g_command_line, 0, 0, FALSE, 0, 0, 0, &startup, &child))
    {
        PrintWinError("run", GetLastError());
        return;
    }

    DWORD wait = WaitForSingleObject(child.hProcess, INFINITE);
    DWORD code = 0;
    if (wait == WAIT_OBJECT_0) GetExitCodeProcess(child.hProcess, &code);
    CloseHandle(child.hThread);
    CloseHandle(child.hProcess);

    if (wait != WAIT_OBJECT_0)
    {
        OutLine("run: the child process did not become signaled");
        return;
    }
    Out("run: exit code ");
    OutUInt(code);
    OutLine("");
}

static void CmdCls(void)
{
    Out("\x1b[2J\x1b[H");
}

static void CmdExit(int argc, char **argv)
{
    DWORD code = argc > 1 ? ParseUInt(argv[1]) : 0;
    ExitProcess(code);
}

// --- dispatch --------------------------------------------------------------------------

static int Tokenize(char *line, char **argv, int capacity)
{
    int argc = 0;
    char *cursor = line;
    while (*cursor)
    {
        while (*cursor == ' ' || *cursor == '\t') cursor++;
        if (!*cursor || argc >= capacity) break;

        if (*cursor == '"')
        {
            cursor++;
            argv[argc++] = cursor;
            while (*cursor && *cursor != '"') cursor++;
        }
        else
        {
            argv[argc++] = cursor;
            while (*cursor && *cursor != ' ' && *cursor != '\t') cursor++;
        }

        if (*cursor)
        {
            *cursor = 0;
            cursor++;
        }
    }
    return argc;
}

static void Dispatch(int argc, char **argv)
{
    if (StrEqI(argv[0], "help") || StrEqI(argv[0], "?"))
        CmdHelp();
    else if (StrEqI(argv[0], "echo"))
        CmdEcho(argc, argv);
    else if (StrEqI(argv[0], "dir") || StrEqI(argv[0], "ls"))
        CmdDir(argc, argv);
    else if (StrEqI(argv[0], "cd") || StrEqI(argv[0], "chdir"))
        CmdCd(argc, argv);
    else if (StrEqI(argv[0], "pwd"))
        CmdPwd();
    else if (StrEqI(argv[0], "type") || StrEqI(argv[0], "cat"))
        CmdType(argc, argv);
    else if (StrEqI(argv[0], "mkdir") || StrEqI(argv[0], "md"))
        CmdMkdir(argc, argv);
    else if (StrEqI(argv[0], "rmdir") || StrEqI(argv[0], "rd"))
        CmdRmdir(argc, argv);
    else if (StrEqI(argv[0], "del") || StrEqI(argv[0], "erase"))
        CmdDel(argc, argv);
    else if (StrEqI(argv[0], "copy"))
        CmdCopy(argc, argv);
    else if (StrEqI(argv[0], "move") || StrEqI(argv[0], "ren") || StrEqI(argv[0], "rename"))
        CmdMove(argc, argv);
    else if (StrEqI(argv[0], "run") || StrEqI(argv[0], "start"))
        CmdRun(argc, argv);
    else if (StrEqI(argv[0], "cls"))
        CmdCls();
    else if (StrEqI(argv[0], "exit") || StrEqI(argv[0], "quit"))
        CmdExit(argc, argv);
    else
    {
        Out("'");
        Out(argv[0]);
        OutLine("' is not recognized. Type 'help' for a list of commands.");
    }
}

// Returns 0 when the command line has no /c switch, 1 when a command was copied, and 2 when /c
// was present but no command followed it.
static int ExtractCommandMode(const char *commandLine, char *command, int capacity)
{
    const char *cursor = commandLine;
    while (*cursor == ' ' || *cursor == '\t') cursor++;

    if (*cursor == '"')
    {
        cursor++;
        while (*cursor && *cursor != '"') cursor++;
        if (*cursor == '"') cursor++;
    }
    else
    {
        while (*cursor && *cursor != ' ' && *cursor != '\t') cursor++;
    }

    while (*cursor == ' ' || *cursor == '\t') cursor++;
    if ((*cursor == '/' || *cursor == '-') &&
        (cursor[1] == 'c' || cursor[1] == 'C') &&
        (cursor[2] == 0 || cursor[2] == ' ' || cursor[2] == '\t'))
    {
        cursor += 2;
        while (*cursor == ' ' || *cursor == '\t') cursor++;

        int length = StrLen(cursor);
        if (length >= 2 && cursor[0] == '"' && cursor[length - 1] == '"')
        {
            cursor++;
            length -= 2;
        }
        if (length <= 0) return 2;
        if (length > capacity - 1) length = capacity - 1;
        for (int i = 0; i < length; i++) command[i] = cursor[i];
        command[length] = 0;
        return 1;
    }
    return 0;
}

void NtEntry(void)
{
    g_in = GetStdHandle(STD_INPUT_HANDLE);
    g_out = GetStdHandle(STD_OUTPUT_HANDLE);

    char line[SHELL_LINE_MAX];
    char *argv[SHELL_ARG_MAX];

    // cmd /c parity: execute one command from the command line and exit without touching stdin.
    LPWSTR commandLineWide = GetCommandLineW();
    if (commandLineWide)
    {
        WideToUtf8(commandLineWide, g_utf8, SHELL_UTF8_MAX);
        int mode = ExtractCommandMode(g_utf8, g_command, SHELL_LINE_MAX);
        if (mode == 2)
        {
            OutLine("shell: /c requires a command");
            ExitProcess(2);
        }
        if (mode == 1)
        {
            CopyString(g_command, line, SHELL_LINE_MAX);
            int argc = Tokenize(line, argv, SHELL_ARG_MAX);
            if (argc > 0) Dispatch(argc, argv);
            ExitProcess(0);
        }
    }

    OutLine("ImaginedNT minimal shell. Type 'help' for commands.");
    for (;;)
    {
        PrintPrompt();
        int length = ReadLine(line, SHELL_LINE_MAX);
        if (length < 0)
        {
            OutLine("");
            ExitProcess(0);
        }
        if (length == 0) continue;

        int argc = Tokenize(line, argv, SHELL_ARG_MAX);
        if (argc > 0) Dispatch(argc, argv);
    }
}
