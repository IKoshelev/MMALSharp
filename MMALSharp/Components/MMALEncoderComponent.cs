﻿using MMALSharp.Handlers;
using MMALSharp.Native;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using static MMALSharp.MMALParameterHelpers;
using static MMALSharp.MMALCallerHelper;

namespace MMALSharp.Components
{
    public abstract unsafe class MMALEncoderBase : MMALDownstreamComponent
    {                
        protected MMALEncoderBase(string encoderName) : base(encoderName) { }

        public void Start()
        {
            this.Outputs.ElementAt(0).EnablePort(this.EncoderOutputCallback);
        }

        public void Stop()
        {
            this.Outputs.ElementAt(0).DisablePort();
        }

        public virtual void EncoderInputCallback(MMALBufferImpl buffer)
        {
            buffer.Release();
        }

        public virtual void EncoderOutputCallback(MMALBufferImpl buffer)
        {
            var data = buffer.GetBufferData();

            if (data != null && this.Storage != null)
                this.Storage = this.Storage.Concat(data).ToArray();
            else if (data != null && this.Storage == null)
                this.Storage = data;
        }

        public override void Initialize()
        {
            var input = this.Inputs.ElementAt(0);
            var output = this.Outputs.ElementAt(0);

            input.ShallowCopy(output);
        }

    }
        
    public unsafe class MMALVideoEncoder : MMALEncoderBase
    {
        public MMALVideoEncoder() : base(MMALParameters.MMAL_COMPONENT_DEFAULT_VIDEO_ENCODER)
        {
            this.Initialize();
        }

        public override void Initialize()
        {
            throw new NotImplementedException();
        }
        
    }

    public unsafe class MMALVideoDecoder : MMALEncoderBase
    {
        public MMALVideoDecoder() : base(MMALParameters.MMAL_COMPONENT_DEFAULT_VIDEO_DECODER)
        {
            this.Initialize();
        }

        public override void Initialize()
        {
            throw new NotImplementedException();
        }
        
    }

    public unsafe class MMALImageEncoder : MMALEncoderBase
    {
        public const int MaxExifPayloadLength = 128;

        public uint EncodingType { get; set; }
        public uint Quality { get; set; }

        public MMALImageEncoder(uint encodingType, uint quality) : base(MMALParameters.MMAL_COMPONENT_DEFAULT_IMAGE_ENCODER)
        {
            this.EncodingType = encodingType;
            this.Quality = quality;
            this.Initialize();
        }

        public MMALImageEncoder() : base(MMALParameters.MMAL_COMPONENT_DEFAULT_IMAGE_ENCODER)
        {
            this.EncodingType = MMALEncodings.MMAL_ENCODING_JPEG;
            this.Quality = 90;
            this.Initialize();
        }
        
        public override void Initialize()
        {
            base.Initialize();
            var input = this.Inputs.ElementAt(0);
            var output = this.Outputs.ElementAt(0);
                        
            output.Ptr->format->encoding = this.EncodingType;
            output.Ptr->bufferNum = Math.Max(output.Ptr->bufferNumRecommended, output.Ptr->bufferNumMin);
            output.Ptr->bufferSize = Math.Max(output.Ptr->bufferSizeRecommended, output.Ptr->bufferSizeMin);

            output.Commit();
                        
            if(this.EncodingType == MMALEncodings.MMAL_ENCODING_JPEG)
                SetParameter(MMALParametersCamera.MMAL_PARAMETER_JPEG_Q_FACTOR, this.Quality, output.Ptr);
        }

        public unsafe void AddExifTag(ExifTag exifTag)
        {
            var formattedExif = exifTag.Key + "=" + exifTag.Value + char.MinValue;

            if (formattedExif.Length > MaxExifPayloadLength)
                throw new PiCameraError("EXIF payload greater than allowed max.");
            
            MMAL_PARAMETER_EXIF_T exifParam = new MMAL_PARAMETER_EXIF_T(new MMAL_PARAMETER_HEADER_T((uint)MMALParametersCamera.MMAL_PARAMETER_EXIF, (uint)(Marshal.SizeOf<MMAL_PARAMETER_EXIF_T>() + formattedExif.Length)),
                                                                        exifTag.Key.Length, exifTag.Key.Length, exifTag.Value.Length, Encoding.ASCII.GetBytes(exifTag.Key + exifTag.Value));
                        
            Console.WriteLine("Preparing to set EXIF");          
            MMALCheck(MMALPort.mmal_port_parameter_set(this.Outputs.ElementAt(0).Ptr, &exifParam.hdr), string.Format("Unable to set EXIF {0}", formattedExif));
            
        }

    }

    public unsafe class MMALImageDecoder : MMALEncoderBase
    {
        public MMALImageDecoder() : base(MMALParameters.MMAL_COMPONENT_DEFAULT_IMAGE_DECODER)
        {
            this.Initialize();
        }

        public override void Initialize()
        {
            throw new NotImplementedException();
        }

    }


}
