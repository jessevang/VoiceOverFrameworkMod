﻿using System;

namespace VoiceOverFrameworkMod.Lib.NVorbis.Contracts.Ogg
{
    [Flags]
    enum PageFlags
    {
        None = 0,
        ContinuesPacket = 1,
        BeginningOfStream = 2,
        EndOfStream = 4,
    }
}
