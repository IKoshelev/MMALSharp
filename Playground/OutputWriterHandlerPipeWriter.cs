using Microsoft.Extensions.Logging;
using MMALSharp.Common;
using MMALSharp.Common.Utility;
using MMALSharp.Handlers;
using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StreamSplitterExperiment
{
    public class OutputWriterHandlerPipeWriter : OutputCaptureHandler, IVideoCaptureHandler
    {
        private readonly PipeWriter PipeWriter;
        public Action? OnPostProcess;
        public bool PauseWritingTillIframe = false;
        public OutputWriterHandlerPipeWriter(PipeWriter pipeWriter)
        {
            this.PipeWriter = pipeWriter;
        }

        public override void Dispose()
        {
            MMALLog.Logger.LogInformation($"Successfully processed {Helpers.ConvertBytesToMegabytes(this.Processed)}.");
            this.PipeWriter.Complete();
        }

        public override void PostProcess()
        {
            this.OnPostProcess?.Invoke();
        }

        public override void Process(ImageContext context)
        {
            //_ = this.PipeWriter.WriteAsync(context.Data, this.CancelationToken).Result;
            if (this.PauseWritingTillIframe)
            {
                if (context.IFrame == false)
                {
                    return;
                }
                else
                {
                    this.PauseWritingTillIframe = false;
                }
            }

            this.PipeWriter.AsStream().Write(context.Data, 0, context.Data.Length);
        }

        public void Split()
        {
            //throw new NotImplementedException();
        }

        public override string TotalProcessed()
        {
            return $"{Helpers.ConvertBytesToMegabytes(this.Processed)}";
        }
    }
}