#ifndef IMAGINEDNT_CONHOST_H
#define IMAGINEDNT_CONHOST_H

#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0A00
#endif
#include <windows.h>

#define CONHOST_CLASS L"ImaginedNT.ConsoleHost"

/* ATTACH: wParam = input receiver HWND, or NULL to restore cmd input. Returns TRUE on success. */
#define CONHOST_ATTACH (WM_APP + 1)
#define CONHOST_CLEAR  (WM_APP + 2)

/* WM_COPYDATA: UTF-16 payload, cbData in bytes, excluding the trailing NUL.
   OUTPUT goes to conhost; INPUT goes to the attached receiver and includes CRLF.
   The receiver must copy the payload before returning TRUE. */
#define CONHOST_OUTPUT 0x43480001
#define CONHOST_INPUT  0x43480002

#endif
