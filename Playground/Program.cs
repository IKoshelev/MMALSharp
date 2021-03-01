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
using System.Numerics;
using System.Linq;
using Intrinsics = System.Runtime.Intrinsics;
using System.Runtime.Intrinsics;

namespace StreamSplitterExperiment
{
    class Program
    {
        async static Task Main(string[] args)
        {
            Console.WriteLine(System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported);

            
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
            MMALCameraConfig.ISO = 800;
            MMALCameraConfig.AwbMode = MMALSharp.Native.MMAL_PARAM_AWBMODE_T.MMAL_PARAM_AWBMODE_AUTO;
            cam.ConfigureCameraSettings();

            var motionAlgorithm = new MotionAlgorithmRGBDiff(
                rgbThreshold: 50,
                cellPixelPercentage: 30,
                cellCountThreshold: 12);

            // Use the default configuration.
            var motionConfig = new MotionConfig(algorithm: motionAlgorithm,
                testFrameInterval: TimeSpan.FromSeconds(3),
                testFrameCooldown: TimeSpan.FromSeconds(3),
                resetTestFrameOnMotion: true);

            FrameBufferCaptureHandler motionProxy = null;

            // Helper method to configure ExternalProcessCaptureHandlerOptions. There are
            // many optional arguments but they are generally optimized for the recommended
            // 640 x 480-based motion detection image stream.
            // This manages the ffmpeg and clvc processes running under a separate bash shell.
            using (var shell = VLCCaptureHandler.StreamRawRGB24asMJPEG())

            // This version of the constructor is specific to running in analysis mode. The null
            // argument could be replaced with a motion detection delegate like those provided to
            // cam.WithMotionDetection() for normal motion detection usage.      
            using (var motion = new FrameBufferCaptureHandler(motionConfig, () =>
            {
                //motionProxy?.WriteFrame();
                Console.WriteLine("Motion!");
            }))

            // Although we've already set the camera resolution, this allows us to specify the raw
            // format required to drive the motion detection algorithm.
            using (var resizer = new MMALIspComponent())
            {
                //motionProxy = motion;
                //motionProxy.FileDirectory = "./";
                //motionProxy.FileExtension = "bmp";
                //motionProxy.FileDateTimeFormat = "yyyy-MM-dd HH.mm.ss.ffff";

                // This tells the algorithm to generate the analysis images and feed them
                // to an output capture handler (in this case our ffmpeg / cvlc pipeline).
                motionAlgorithm.EnableAnalysis(shell);

                resizer.ConfigureOutputPort<VideoPort>(0, new MMALPortConfig(MMALEncoding.RGB24, MMALEncoding.RGB24, width: 640, height: 480), motion);
                cam.Camera.VideoPort.ConnectTo(resizer);

                // Camera warm-up.
                await Task.Delay(2000);//cameraWarmupDelay(cam);

                // Tell the user how to connect to the MJPEG stream.
                Console.WriteLine($"Streaming MJPEG with motion detection analysis for {60} sec to:");
                Console.WriteLine($"http://{Environment.MachineName}.local:8554/");

                // Set the duration and let it run...
                var stoppingToken = new CancellationTokenSource(TimeSpan.FromSeconds(100));
                await Task.WhenAll(new Task[]{
                shell.ProcessExternalAsync(stoppingToken.Token),
                cam.ProcessAsync(cam.Camera.VideoPort, stoppingToken.Token),
            }).ConfigureAwait(false);
            }
            cam.Cleanup();


            var res = VectorTest(
                new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 },
                new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 });

            Console.WriteLine(res);

            PerformTest(); 
            PerformTest();
            PerformTest();
            PerformTest();
            PerformTest();

        }

        private static void PerformTest()
        {
            Console.WriteLine("Test:");
            var arr1 = RandomArr((4 * 1024 * 1024));
            var arr2 = RandomArr((4 * 1024 * 1024));

            var stopwatch = new Stopwatch();

            stopwatch.Start();
            var resVec = VectorTest(arr1, arr2);
            stopwatch.Stop();
            Console.WriteLine($"resVec:{resVec}; Ms: {stopwatch.ElapsedMilliseconds}");

            stopwatch.Reset();
            stopwatch.Start();
            var resItr = IntrinsicTest(arr1, arr2);
            stopwatch.Stop();
            Console.WriteLine($"resItr:{resItr}; Ms: {stopwatch.ElapsedMilliseconds}");

            stopwatch.Reset();
            stopwatch.Start();
            var resScal = ScalarTest(arr1, arr2);
            stopwatch.Stop();
            Console.WriteLine($"resScl:{resScal}; Ms: {stopwatch.ElapsedMilliseconds}");
        }

        public static byte[] RandomArr(int length)
        {
            var arr = new byte[length];
            new Random().NextBytes(arr);
            return arr;
        }

        public static int VectorTest(byte[] lhs, byte[] rhs)
        {
            var simdLength = Vector<byte>.Count;

            if (simdLength > 255)
            {
                throw new Exception("Dot product may overflow");
            }

            var mask = new byte[lhs.Length];
            System.Array.Fill(mask, (byte)16);
            var threshold = new Vector<byte>(mask); //new byte[lhs.Length].

            System.Array.Fill(mask, (byte)1);
            var bitwiseMasks = new Vector<byte>(mask);

            var VECTOR_ONE = Vector<byte>.One;

            var result = 0;
            var i = 0;
            for (i = 0; i <= lhs.Length - simdLength; i += simdLength)
            {
                var va = new Vector<byte>(lhs, i);
                var vb = new Vector<byte>(rhs, i);
                var vmax = Vector.Max(va, vb);
                var vmin = Vector.Min(va, vb);
                var subtracted = (vmax - vmin);
                //Console.WriteLine(String.Join(",", subtracted));
                var thresholdPassed = Vector.GreaterThanOrEqual(subtracted, threshold);
                var normalized = Vector.BitwiseAnd(thresholdPassed, bitwiseMasks);

                result += Vector.Dot(normalized, VECTOR_ONE);
            }

            return result;
        }

        /// <summary>
        /// Make sure array length is multiple of 16 and both equal length
        /// </summary>
        /// <param name="lhs"></param>
        /// <param name="rhs"></param>
        /// <returns></returns>
        public static int IntrinsicTest(byte[] lhs, byte[] rhs)
        {
            byte ths = 16;
            Intrinsics.Vector128<byte> threshold = Intrinsics.Vector128.Create(ths, ths, ths, ths, ths, ths, ths, ths, ths, ths, ths, ths, ths, ths, ths, ths);
            byte bit = 1;
            var VECTOR_ONE = Intrinsics.Vector128.Create(bit, bit, bit, bit, bit, bit, bit, bit, bit, bit, bit, bit, bit, bit, bit, bit);

            var result = new Intrinsics.Vector128<int>();
            var i = 0;
            for (i = 0; i <= lhs.Length - 16; i += 16)
            {
                //using System.Numerics.Vector for loading, then converting to Intrinsics.Vector128
                var v128a = new Vector<byte>(lhs, i).AsVector128();
                var v128b = new Vector<byte>(rhs, i).AsVector128();

                var subtracted = Intrinsics.Arm.AdvSimd.AbsoluteDifference(v128a, v128b);
                //Console.WriteLine(subtracted.ToString());

                var thresholdPassed = Intrinsics.Arm.AdvSimd.CompareGreaterThanOrEqual(subtracted, threshold);

                var normalized = Intrinsics.Vector128.AsSByte(thresholdPassed);    //Intrinsics.Arm.AdvSimd.And(thresholdPassed, VECTOR_ONE);
                //Console.WriteLine(normalized);

                var rtemp = Intrinsics.Arm.AdvSimd.AddPairwiseWidening(normalized);
                result = Intrinsics.Arm.AdvSimd.AddPairwiseWideningAndAdd(result, rtemp);
                //Console.WriteLine(result);
            }

            //Console.WriteLine(r4);
            //Console.WriteLine(r5);
            return -1 * (int)(Intrinsics.Arm.AdvSimd.Extract(result, 0)
                            + Intrinsics.Arm.AdvSimd.Extract(result, 1)
                            + Intrinsics.Arm.AdvSimd.Extract(result, 2)
                            + Intrinsics.Arm.AdvSimd.Extract(result, 3));
        }

        public static int ScalarTest(byte[] lhs, byte[] rhs)
        {
            var result = 0;

            for (int index = 0; index < lhs.Length; index++)
            {
                var a = lhs[index];
                var b = rhs[index];
                if (b > a)
                {
                    (b, a) = (a, b);
                }
                result += ((a - b) >= 16) ? 1 : 0;
            }

            return result;
        }

    }

    public class Symaphore
    {
        public bool StreamVideo = false;
        public Action? RequestIFrame = null;
        public bool StreamPhoto = false;
    }
}