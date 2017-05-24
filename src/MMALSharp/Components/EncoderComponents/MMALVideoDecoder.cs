﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MMALSharp.Components.EncoderComponents;
using MMALSharp.Handlers;
using MMALSharp.Native;

namespace MMALSharp.Components
{
    /// <summary>
    /// Represents a video decoder component
    /// </summary>
    public sealed unsafe class MMALVideoDecoder : MMALEncoderBase
    {
        public MMALVideoDecoder(ICaptureHandler handler) : base(MMALParameters.MMAL_COMPONENT_DEFAULT_VIDEO_DECODER, handler)
        {
            throw new NotImplementedException();
        }
    }
}
