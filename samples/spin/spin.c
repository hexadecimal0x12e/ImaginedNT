#include <windows.h>

/*
 * A deliberately CPU-bound guest.  It never returns and never sleeps: the only way to stop it
 * is the host watchdog / --timeout path (or an external TerminateProcess).  Run it with:
 *
 *   ImaginedNT.Host run samples/bin/spin.exe --strict --timeout 2 --verbose
 *
 * The sampled GetTickCount64 call proves the guest is still entering the managed API layer
 * while the CPU is spinning.
 */
void NtEntry(void)
{
    volatile unsigned long long spins = 0;
    for (;;)
    {
        spins++;
        if ((spins & 0x0FFFFFFFULL) == 0)
            GetTickCount64();
    }
}
