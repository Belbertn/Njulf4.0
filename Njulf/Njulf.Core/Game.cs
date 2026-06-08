using System;
using Microsoft.Extensions.DependencyInjection;
using Njulf.Core.Camera;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;

namespace Njulf.Core
{
    public abstract class Game : IDisposable
    {
        private IServiceProvider? _services;
        private IRenderer? _renderer;
        private IContentManager? _content;
        private IInputManager? _input;
        private ICamera? _camera;
        private Scene.Scene _scene = null!;
        private bool _isRunning = false;
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "IDE0052:Remove unread private members", Justification = "Used for initialization tracking")]
        private bool _isInitialized = false;

        public string Name { get; set; } = "Njulf Game";
        public int WindowWidth { get; set; } = 1280;
        public int WindowHeight { get; set; } = 720;
        public string WindowTitle { get; set; } = "Njulf Game";
        public bool VSync { get; set; } = true;
        public bool IsRunning => _isRunning;

        public IServiceProvider? Services => _services;
        public IRenderer? Renderer => _renderer;
        public IContentManager? Content => _content;
        public IInputManager? Input => _input;
        public ICamera? Camera => _camera;
        public Scene.Scene Scene => _scene;

        protected Game()
        {
            _scene = new Scene.Scene();
        }

        public void Run()
        {
            if (_isRunning) return;
            _isRunning = true;

            try
            {
                Initialize();
                _isInitialized = true;
                Load();

                RunMainLoop();

                Unload();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
                throw;
            }
            finally
            {
                _isRunning = false;
            }
        }

        protected virtual void Initialize()
        {
            var services = new ServiceCollection();
            ConfigureServices(services);
            _services = services.BuildServiceProvider();

            _renderer = _services.GetService<IRenderer>()!;
            _content = _services.GetService<IContentManager>()!;
            _input = _services.GetService<IInputManager>()!;
            _camera = _services.GetService<ICamera>() ?? CreateDefaultCamera();

            _renderer?.Initialize();
        }

        protected virtual void ConfigureServices(IServiceCollection services)
        {
        }

        protected virtual ICamera CreateDefaultCamera()
        {
            return new FirstPersonCamera(new Vector3(0, 0, 5));
        }

        protected virtual void Load()
        {
        }

        protected virtual void RunMainLoop()
        {
            while (_isRunning)
            {
                UpdateFrame();
                DrawFrame();
            }
        }

        protected virtual void UpdateFrame()
        {
            _input?.Update();
            Update(1f / 60f);
        }

        protected virtual void DrawFrame()
        {
            _renderer?.BeginFrame();
            Draw();
            _renderer?.EndFrame();
        }

        protected virtual void Update(float deltaTime)
        {
            _scene.Update(deltaTime);
        }

        protected virtual void Draw()
        {
        }

        protected virtual void Unload()
        {
            _renderer?.Dispose();
            _scene?.Dispose();
        }

        protected virtual void OnResize(int width, int height)
        {
            WindowWidth = width;
            WindowHeight = height;
            _renderer?.Resize(width, height);
            if (_camera != null)
            {
                _camera.AspectRatio = (float)width / height;
            }
        }

        public void Exit()
        {
            _isRunning = false;
        }

        public void Dispose()
        {
            Exit();
            _renderer?.Dispose();
            _scene?.Dispose();
        }
    }
}
