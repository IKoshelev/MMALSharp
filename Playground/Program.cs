using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Pipelines;
using System.Buffers;
using MMALSharp;
using MMALSharp.Handlers;
using MMALSharp.Components;
using MMALSharp.Ports;
using MMALSharp.Common;

namespace StreamSplitterExperiment
{
    class Program
    {
        async static Task Main(string[] args)
        {
            var symaphore = new Symaphore();

            Console.WriteLine("running");
            TcpListener server1 = new TcpListener(IPAddress.Any, 9010);
            NetworkStream? activeClient1 = null;
            server1.Start();

            _ = Task.Run(async () =>
            {
                while (true)
                {
                    //we are responsible for never having more than 1 client
                    activeClient1 = (await server1.AcceptTcpClientAsync()).GetStream();
                    symaphore.StreamVideo = true;
                    var requestIFrame = symaphore.RequestIFrame;
                    requestIFrame?.Invoke();
                    Console.WriteLine("Connected 1");
                }
            });

            TcpListener server2 = new TcpListener(IPAddress.Any, 9011);
            NetworkStream? activeClient2 = null;
            server2.Start();

            _ = Task.Run(async () =>
            {
                while (true)
                {
                    //we are responsible for never having more than 1 client
                    activeClient2 = (await server2.AcceptTcpClientAsync()).GetStream();
                    symaphore.StreamPhoto = true;
                    Console.WriteLine("Connected 2");
                }
            });

            var pipeH264 = new Pipe();
            var writerH264 = new OutputWriterHandlerPipeWriter(pipeH264.Writer);

            _ = Task.Run(async () =>
            {
                while (true)
                {
                    ReadResult result = await pipeH264.Reader.ReadAsync();
                    ReadOnlySequence<byte> buffer = result.Buffer;

                    if (activeClient1 != null)
                    {
                        //Console.WriteLine($"pipeH264 write {buffer.Length}");
                        await activeClient1.WriteAsync(buffer.ToArray());
                        await activeClient1.FlushAsync();
                    }

                    // advance even when no client, to avoid piple-up
                    pipeH264.Reader.AdvanceTo(buffer.End);
                }
            });

            var pipeMJPEG = new Pipe();
            var writerMJPEG = new OutputWriterHandlerPipeWriter(pipeMJPEG.Writer);

            _ = Task.Run(async () =>
            {
                while (true)
                {
                    if (activeClient2 != null)
                    {
                        ReadResult result = await pipeMJPEG.Reader.ReadAsync();
                        ReadOnlySequence<byte> buffer = result.Buffer;

                        Console.WriteLine($"pipeMJPEG write {buffer.Length}");
                        await activeClient2.WriteAsync(buffer.ToArray());
                        await activeClient2.FlushAsync();

                        // advance even when no client, to avoid piple-up
                        pipeMJPEG.Reader.AdvanceTo(buffer.End);
                    }
                    else
                    {
                        await Task.Delay(100);
                    }
                }
            });

            var token = new CancellationTokenSource().Token;
            await CaptureDifferentStreams(writerH264, writerMJPEG, symaphore, token);

        }

        public static async Task CaptureDifferentStreams(
            OutputWriterHandlerPipeWriter h264PipeWriter,
            OutputWriterHandlerPipeWriter mjpegPipeWriter,
            Symaphore symaphore,
            CancellationToken token = default)
        {
            MMALCamera cam = MMALCamera.Instance;

            MMALCameraConfig.InlineHeaders = true;

            cam.ConfigureCameraSettings();

            await Task.Delay(2000);

            symaphore.StreamVideo = false;
            using (var vidEncoder = new MMALVideoEncoder())
            {
                var portConfig = new MMALPortConfig(MMALEncoding.H264, MMALEncoding.I420, 25, 0, null);

                vidEncoder.ConfigureOutputPort(portConfig, h264PipeWriter);

                // Create our component pipeline.
                cam.Camera.VideoPort.ConnectTo(vidEncoder);
                //this.Camera.PreviewPort.ConnectTo(renderer);

                symaphore.RequestIFrame = () =>
                {
                    Console.WriteLine("Requesting IFrame");
                    vidEncoder.RequestIFrame();
                    h264PipeWriter.PauseWritingTillIframe = true;
                };

                var source = new CancellationTokenSource(TimeSpan.FromSeconds(100));
                Console.WriteLine($"starting vid stream");
                await cam.ProcessAsync(cam.Camera.VideoPort, source.Token).ConfigureAwait(false);
                Console.WriteLine($"ending vid stream");
            }

            cam.Cleanup();
        }
    }

    public class Symaphore
    {
        public bool StreamVideo = false;
        public Action? RequestIFrame = null;
        public bool StreamPhoto = false;
    }
}