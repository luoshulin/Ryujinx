﻿using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.Platform;
using Avalonia.Win32;
using OpenTK.Graphics.OpenGL;
using Ryujinx.Common.Configuration;
using SPB.Graphics;
using SPB.Graphics.OpenGL;
using SPB.Platform;
using SPB.Platform.Win32;
using SPB.Windowing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ryujinx.Ava.Ui.Controls
{
    internal class OpenGlRenderer : RendererControl
    {
        private IntPtr _handle;
        private SwappableNativeWindowBase _window;
        private int _framebuffer;

        public int Major { get; }
        public int Minor { get; }
        public GraphicsDebugLevel DebugLevel { get; }
        public OpenGLContextBase Context { get; set; }

        public OpenGLContextBase PrimaryContext => 
                AvaloniaLocator.Current.GetService<IPlatformOpenGlInterface>().PrimaryContext.AsOpenGLContextBase();

        public OpenGlRenderer(int major, int minor, GraphicsDebugLevel graphicsDebugLevel)
        {
            Major = major;
            Minor = minor;
            DebugLevel = graphicsDebugLevel;
        }

        protected override void OnOpenGlInit(GlInterface gl, int fb)
        {
            base.OnOpenGlInit(gl, fb);

            OpenGLContextBase mainContext = PrimaryContext;

            CreateWindow(mainContext);

            Window.SwapInterval = 0;

            OnInitialized(gl);

            _framebuffer = GL.GenFramebuffer();
        }

        protected override void OnRender(GlInterface gl, int fb)
        {
            if(Image == 0)
            {
                return;
            }

            GL.Clear(ClearBufferMask.ColorBufferBit);
            GL.ClearColor(0,0, 0, 0);

            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _framebuffer);
            GL.FramebufferTexture2D(FramebufferTarget.ReadFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, Image, 0);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fb);
            GL.BlitFramebuffer(0,
                               0,
                               (int)RenderSize.Width,
                               (int)RenderSize.Height,
                               0,
                               (int)RenderSize.Height,
                               (int)RenderSize.Width,
                               0,
                               ClearBufferMask.ColorBufferBit,
                               BlitFramebufferFilter.Linear);

            GL.FramebufferTexture2D(FramebufferTarget.ReadFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, 0, 0);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fb);
        }

        protected override void OnOpenGlDeinit(GlInterface gl, int fb)
        {
            base.OnOpenGlDeinit(gl, fb);

            Window.SwapInterval = 1;
        }

        public async Task DestroyBackgroundContext()
        {
            await Task.Delay(1000);
            // WGL hangs here when disposing context
            //Context?.Dispose();
            _window?.Dispose();
        }

        internal void MakeCurrent()
        {
           Context.MakeCurrent(_window);
        }
        internal void MakeCurrent(SwappableNativeWindowBase window)
        {
            Context.MakeCurrent(window);
        }

        private void CreateWindow(OpenGLContextBase mainContext)
        {
            var flags = OpenGLContextFlags.Compat;
            if(DebugLevel != GraphicsDebugLevel.None)
            {
                flags |= OpenGLContextFlags.Debug;
            }
            _window = PlatformHelper.CreateOpenGLWindow(FramebufferFormat.Default, 0, 0, (int)Bounds.Width, (int)Bounds.Height);
            _window.Hide();

            Context = PlatformHelper.CreateOpenGLContext(FramebufferFormat.Default, Major, Minor, flags, shareContext: mainContext);
            Context.Initialize(_window);
        }
    }
}