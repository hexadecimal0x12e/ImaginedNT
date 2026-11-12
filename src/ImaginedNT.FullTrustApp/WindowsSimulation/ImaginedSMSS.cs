using ImaginedNT.Core.Runtime;
using System;
using System.Collections.Generic;
using System.Text;

namespace ImaginedNT.FullTrustApp.WindowsSimulation
{
    internal class ImaginedSMSS : ImaginedSystemProcessBase
    {
        public override int Execute(ImaginedProcessContext context, CancellationToken ct)
        {
            ct.WaitHandle.WaitOne();
            return 0;
        }
    }
}
