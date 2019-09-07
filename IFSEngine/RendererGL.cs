﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using IFSEngine.Animation;

//using OpenGL;
using OpenTK;
using OpenTK.Graphics.OpenGL;

using IFSEngine.Model;
using IFSEngine.Model.Camera;
using System.ComponentModel;
using IFSEngine.Model.GpuStructs;

namespace IFSEngine
{
    public class RendererGL
    {
        public event EventHandler DisplayFrameCompleted;
        //public event EventHandler RenderFrameCompleted;

        public bool UpdateDisplayOnRender { get; set; } = true;
        public int Framestep { get; private set; } = 0;
        public int Width => ActiveView.Camera.Width;
        public int Height => ActiveView.Camera.Height;

        public IFS CurrentParams { get; set; }
        public IFSView ActiveView {
            get => activeView;
            set {
                //activeView.PropertyChanged -= HandleInvalidation;
                activeView = value;
                activeView.PropertyChanged += HandleInvalidation;
            }
        }
        public AnimationManager AnimationManager { get; set; }

        private const int threadcnt = 1500;//TODO: make this a setting or make it adaptive

        private bool invalidAccumulation = false;
        private bool invalidParams = false;
        private int pass_iters;
        private bool updateDisplayNow = false;
        private bool rendering = false;

        //compute handlers
        private int computeProgramH;
        private int histogramH;
        private int settingsbufH;
        private int itersbufH;
        private int pointsbufH;
        private int palettebufH;
        private int tfparamsbufH;
        private int xaosbufH;
        private int last_tf_index_bufH;
        //display handlers
        private int dispTexH;
        private int displayProgramH;
        private IFSView activeView;

        public RendererGL(IFS Params) : this(Params, Params.Views.First().Camera.Width, Params.Views.First().Camera.Height) { }
        public RendererGL(IFS Params, int w, int h)
        {
            AnimationManager = new AnimationManager();
            CurrentParams = Params;
            ActiveView = CurrentParams.Views.First();
            ActiveView.Camera.Width = w;
            ActiveView.Camera.Height = h;
            invalidParams = true;
            invalidAccumulation = true;
            //TODO: separate opengl initialization from ctor
            initDisplay();
            initRenderer();

        }

        private void HandleInvalidation(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case "1":
                    InvalidateAccumulation();
                    break;
                case "2":
                    InvalidateParams();
                    break;
                case "0":
                default:
                    break;

            }
        }

        public void InvalidateAccumulation()
        {
            //can be called multiple times, but it's enough to reset once before first frame
            invalidAccumulation = true;

        }
        public void InvalidateParams()
        {
            //can be called multiple times, but it's enough to reset once before first frame
            InvalidateAccumulation();
            invalidParams = true;
        }

        //TODO: remove this
        public void Reset()
        {
            CurrentParams.RandomizeParams();
            ActiveView = CurrentParams.Views.First();
            ActiveView.ResetCamera();
            //ActiveView.Camera.OnManipulate += InvalidateAccumulation;
            invalidParams = true;
            invalidAccumulation = true;
        }

        private void UpdatePointsState()
        {
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, pointsbufH);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, 4 * threadcnt * sizeof(float), StartingDistributions.UniformUnitCube(threadcnt), BufferUsageHint.DynamicCopy);
        }

        public void RenderFrame()
        {
            GL.UseProgram(computeProgramH);

            if (invalidAccumulation)
            {
                //reset accumulation
                GL.BindBuffer(BufferTarget.ShaderStorageBuffer, histogramH);
                GL.ClearNamedBufferData(histogramH, PixelInternalFormat.R32f, PixelFormat.Red, PixelType.Float, IntPtr.Zero);
                invalidAccumulation = false;
                pass_iters = 500;//increases by each frame
                Framestep = 0;

                if (invalidParams)
                {
                    UpdatePointsState();

                    //update iterators
                    //generate iterator and transform structs
                    List<IteratorStruct> its = new List<IteratorStruct>();
                    List<float> tfsparams = new List<float>();
                    foreach (var it in CurrentParams.Iterators)
                    {
                        //iterators
                        its.Add(new IteratorStruct
                        {
                         tfId = it.Transform.Id,
                         tfParamsStart = tfsparams.Count,
                         wsum = (float)it.WeightTo.Sum(xw=>xw.Value*xw.Key.baseWeight),
                         cs = (float)it.cs,
                         ci = (float)it.ci,
                         op = (float)it.op,
                        });
                        //transform params
                        List<double> tfiparams = it.Transform.GetListOfParams();
                        tfsparams.AddRange(tfiparams.Select(p=>(float)p));
                    }
                    //TODO: tfparamstart pop last value

                    GL.BindBuffer(BufferTarget.ShaderStorageBuffer, itersbufH);
                    GL.BufferData(BufferTarget.ShaderStorageBuffer, its.Count * (4*sizeof(int)+4*sizeof(float)), its.ToArray(), BufferUsageHint.DynamicDraw);

                    GL.BindBuffer(BufferTarget.ShaderStorageBuffer, tfparamsbufH);
                    GL.BufferData(BufferTarget.ShaderStorageBuffer, tfsparams.Count * sizeof(float), tfsparams.ToArray(), BufferUsageHint.DynamicDraw);

                    //generate flattened xaos weight matrix
                    List<float> xaosm = new List<float>(CurrentParams.Iterators.Count * CurrentParams.Iterators.Count);
                    foreach (var it in CurrentParams.Iterators)
                    {
                        foreach (var toIt in CurrentParams.Iterators)
                        {
                            xaosm.Add((float)(it.WeightTo[toIt]*toIt.baseWeight));
                        }
                    }
                    GL.BindBuffer(BufferTarget.ShaderStorageBuffer, xaosbufH);
                    GL.BufferData(BufferTarget.ShaderStorageBuffer, xaosm.Capacity * sizeof(float), xaosm.ToArray(), BufferUsageHint.DynamicDraw);

                    //regenerated by shader in 1st iteration
                    GL.BindBuffer(BufferTarget.ShaderStorageBuffer, last_tf_index_bufH);
                    GL.BufferData(BufferTarget.ShaderStorageBuffer, threadcnt * sizeof(int), IntPtr.Zero, BufferUsageHint.DynamicCopy);

                    //update palette
                    GL.BindBuffer(BufferTarget.ShaderStorageBuffer, palettebufH);
                    GL.BufferData(BufferTarget.ShaderStorageBuffer, CurrentParams.Palette.Colors.Count * sizeof(float) * 4, CurrentParams.Palette.Colors.ToArray(), BufferUsageHint.DynamicDraw);

                    invalidParams = false;
                }
            }

            if (Framestep % (10000 / pass_iters) == 0)//TODO: fix condition
            {
                UpdatePointsState();
                //idea: place new random points along the most dense area?
                //idea: place new random points along the least dense area?
            }

            var settings = new Settings
            {
                CameraBase = ActiveView.Camera.Params,
                itnum = CurrentParams.Iterators.Count,
                pass_iters = pass_iters,
                framestep = Framestep,
                fog_effect = (float)ActiveView.FogEffect,
                dof = (float)ActiveView.Dof,
                focusdistance = (float)ActiveView.FocusDistance,
                focusarea = (float)ActiveView.FocusArea,
                focuspoint = ActiveView.Camera.Params.position + (float)ActiveView.FocusDistance * ActiveView.Camera.Params.forward,
                palettecnt = CurrentParams.Palette.Colors.Count
            };
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, settingsbufH);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, 4 * sizeof(int) + (24 + 8) * sizeof(float), ref settings, BufferUsageHint.DynamicDraw);

            GL.Finish();
            GL.DispatchCompute(threadcnt, 1, 1);

            pass_iters = Math.Min((int)(pass_iters * 1.5), 1000);
            Framestep++;

            //GL.Finish();

            if (updateDisplayNow || UpdateDisplayOnRender)
            {
                GL.UseProgram(displayProgramH);
                GL.Uniform1(GL.GetUniformLocation(displayProgramH, "framestep"), Framestep);
                GL.Uniform1(GL.GetUniformLocation(displayProgramH, "Brightness"), (float)ActiveView.Brightness);
                GL.Uniform1(GL.GetUniformLocation(displayProgramH, "Gamma"), (float)ActiveView.Gamma);

                //draw quad
                GL.Begin(PrimitiveType.Quads);
                GL.Vertex2(0, 0);
                GL.Vertex2(0, 1);
                GL.Vertex2(1, 1);
                GL.Vertex2(1, 0);
                GL.End();

                DisplayFrameCompleted?.Invoke(this, null);
                updateDisplayNow = false;
            }

        }

        public void StartRendering()
        {
            if (!rendering)
            {
                rendering = true;

                //take context from main thread
                OpenTK.Graphics.GraphicsContext.CurrentContext.MakeCurrent(null);

                new System.Threading.Thread(() =>
                {
                    while (rendering)
                        RenderFrame();
                }).Start();
            }
        }

        public void StopRendering()
        {
            rendering = false;
            GL.Finish();
        }

        public void UpdateDisplay()
        {
            updateDisplayNow = true;
            //TODO: if (!rendering) RenderFrame();
        }

        public double[,][] GenerateImage()
        {
            float[] d = new float[Width * Height * 4];//rgba

            UpdateDisplay();//RenderFrame() if needed

            //Read display texture
            GL.Finish();
            GL.UseProgram(displayProgramH);
            GL.ReadPixels(0, 0, Width, Height, PixelFormat.Rgba, PixelType.Float, d);

            double[,][] o = new double[Width, Height][];
            for (int x = 0; x < Width; x++)
                for (int y = 0; y < Height; y++)
                {
                    o[x, y] = new double[4];
                    o[x, y][0] = d[x * 4 + y * 4 * Width + 0];
                    o[x, y][1] = d[x * 4 + y * 4 * Width + 1];
                    o[x, y][2] = d[x * 4 + y * 4 * Width + 2];
                    o[x, y][3] = d[x * 4 + y * 4 * Width + 3];
                }

            //TODO: image save in netstandard?

            return o;
        }

        private void initDisplay()
        {
            //init display image texture
            dispTexH = GL.GenTexture();
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, dispTexH);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, Width, Height, 0, PixelFormat.Rgba, PixelType.Float, new IntPtr(0));
            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
                throw new GraphicsException("Frame Buffer Error");

            var assembly = typeof(RendererGL).GetTypeInfo().Assembly;

            var vertexShader = GL.CreateShader(ShaderType.VertexShader);
            GL.ShaderSource(vertexShader, new StreamReader(assembly.GetManifestResourceStream("IFSEngine.glsl.Display.vert")).ReadToEnd());
            GL.CompileShader(vertexShader);
            GL.GetShader(vertexShader, ShaderParameter.CompileStatus, out int status);
            if (status == 0)
            {
                throw new GraphicsException(
                    String.Format("Error compiling {0} shader: {1}", ShaderType.VertexShader.ToString(), GL.GetShaderInfoLog(vertexShader)));
            }

            var fragmentShader = GL.CreateShader(ShaderType.FragmentShader);
            string elo = $"#version 450 \n #extension GL_ARB_explicit_attrib_location : enable \n";
            elo += $"uniform int width={Width}; \n uniform int height={Height};\n";
            GL.ShaderSource(fragmentShader, elo + new StreamReader(assembly.GetManifestResourceStream("IFSEngine.glsl.Display.frag")).ReadToEnd());
            GL.CompileShader(fragmentShader);
            GL.GetShader(fragmentShader, ShaderParameter.CompileStatus, out status);
            if (status == 0)
            {
                throw new GraphicsException(
                    String.Format("Error compiling {0} shader: {1}", ShaderType.VertexShader.ToString(), GL.GetShaderInfoLog(fragmentShader)));
            }
            displayProgramH = GL.CreateProgram();
            GL.AttachShader(displayProgramH, vertexShader);
            GL.AttachShader(displayProgramH, fragmentShader);
            GL.LinkProgram(displayProgramH);
            GL.GetProgram(displayProgramH, GetProgramParameterName.LinkStatus, out status);
            if (status == 0)
            {
                throw new GraphicsException(
                    String.Format("Error linking program: {0}", GL.GetProgramInfoLog(displayProgramH)));
            }

            GL.DetachShader(displayProgramH, vertexShader);
            GL.DetachShader(displayProgramH, fragmentShader);
            GL.DeleteShader(vertexShader);
            GL.DeleteShader(fragmentShader);

            GL.UseProgram(displayProgramH);

            histogramH = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, histogramH);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, Width * Height * 4 * sizeof(float), IntPtr.Zero, BufferUsageHint.DynamicRead);

            GL.Viewport(0, 0, Width, Height);
        }

        private void initRenderer()
        {

            //assemble source string
            var resource = typeof(RendererGL).GetTypeInfo().Assembly.GetManifestResourceStream("IFSEngine.glsl.ifs_kernel.compute");
            string computeShaderSource = "";//TODO: add consts here
            computeShaderSource += new StreamReader(resource).ReadToEnd();

            //compile compute shader
            int computeShaderH = GL.CreateShader(ShaderType.ComputeShader);
            GL.ShaderSource(computeShaderH, computeShaderSource);
            GL.CompileShader(computeShaderH);
            Console.WriteLine(GL.GetShaderInfoLog(computeShaderH));

            //build shader program
            computeProgramH = GL.CreateProgram();
            GL.AttachShader(computeProgramH, computeShaderH);
            GL.LinkProgram(computeProgramH);
            Console.WriteLine(GL.GetProgramInfoLog(computeProgramH));
            GL.UseProgram(computeProgramH);

            //create buffers
            pointsbufH = GL.GenBuffer();
            settingsbufH = GL.GenBuffer();
            itersbufH = GL.GenBuffer();
            palettebufH = GL.GenBuffer();
            tfparamsbufH = GL.GenBuffer();
            xaosbufH = GL.GenBuffer();
            last_tf_index_bufH = GL.GenBuffer();

            //bind layout:
            GL.BindImageTexture(0, dispTexH, 0, false, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba32f);//TODO: use this or remove
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, histogramH);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, pointsbufH);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, settingsbufH);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 4, itersbufH);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, palettebufH);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 6, tfparamsbufH);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 7, xaosbufH);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 8, last_tf_index_bufH);


            //set uniforms
            GL.Uniform1(GL.GetUniformLocation(computeProgramH, "width"), Width);
            GL.Uniform1(GL.GetUniformLocation(computeProgramH, "height"), Height);

        }

        public void Dispose()
        {
            StopRendering();
            //TOOD: dispose
        }

    }
}
