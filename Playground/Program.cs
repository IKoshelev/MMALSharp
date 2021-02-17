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
using MMALSharp.Common.Utility;
using MMALSharp.Processors.Motion;
using MMALSharp.Ports.Outputs;

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
            var cam = MMALCamera.Instance;

            // Set 640 x 480 at 20 FPS.
            MMALCameraConfig.Resolution = new Resolution(640, 480);
            MMALCameraConfig.SensorMode = MMALSensorMode.Mode7;
            MMALCameraConfig.Framerate = 20;
            cam.ConfigureCameraSettings();

            var motionAlgorithm = new MotionAlgorithmRGBDiff();

            // Use the default configuration.
            var motionConfig = new MotionConfig(algorithm: motionAlgorithm);

            // Helper method to configure ExternalProcessCaptureHandlerOptions. There are
            // many optional arguments but they are generally optimized for the recommended
            // 640 x 480-based motion detection image stream.
            // This manages the ffmpeg and clvc processes running under a separate bash shell.
            using (var shell = VLCCaptureHandler.StreamRawRGB24asMJPEG())

            // This version of the constructor is specific to running in analysis mode. The null
            // argument could be replaced with a motion detection delegate like those provided to
            // cam.WithMotionDetection() for normal motion detection usage.
            using (var motion = new FrameBufferCaptureHandler(motionConfig, null))

            // Although we've already set the camera resolution, this allows us to specify the raw
            // format required to drive the motion detection algorithm.
            using (var resizer = new MMALIspComponent())
            {
                // This tells the algorithm to generate the analysis images and feed them
                // to an output capture handler (in this case our ffmpeg / cvlc pipeline).
                motionAlgorithm.EnableAnalysis(shell);

                resizer.ConfigureOutputPort<VideoPort>(0, new MMALPortConfig(MMALEncoding.RGB24, MMALEncoding.RGB24, width: 640, height: 480), motion);
                cam.Camera.VideoPort.ConnectTo(resizer);

                // Camera warm-up.
                await Task.Delay(2000);//cameraWarmupDelay(cam);

                // Tell the user how to connect to the MJPEG stream.
                Console.WriteLine($"Streaming MJPEG with motion detection analysis for {30} sec to:");
                Console.WriteLine($"http://{Environment.MachineName}.local:8554/");

                // Set the duration and let it run...
                var stoppingToken = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await Task.WhenAll(new Task[]{
                shell.ProcessExternalAsync(stoppingToken.Token),
                cam.ProcessAsync(cam.Camera.VideoPort, stoppingToken.Token),
            }).ConfigureAwait(false);
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