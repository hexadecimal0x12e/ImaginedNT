using ImaginedNT.Core.Runtime;
using System;
using System.Collections.Generic;
using System.Text;

namespace ImaginedNT.FullTrustApp.WindowsSimulation
{
    internal abstract class ImaginedSystemProcessBase
    {
        public abstract int Execute(ImaginedProcessContext context, CancellationToken cancellationToken);

        public ImaginedProcess Start(ImaginedRuntime runtime, ProcessCreateOptions options)
        {
            return runtime.StartImaginedProcess(options, (f, c) =>
            {
                c.Register(() => runtime.StopWithBugCheck($"You killed a system process {Path.GetFileName(options.ImagePath)}. In real NT kernel, you'll result in a bug check CRITICAL_PROCESS_DEAD."));
                return Execute(f, c);
            });
        }

    }
}
