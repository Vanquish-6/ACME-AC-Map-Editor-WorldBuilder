using Acme.Render;
using Microsoft.Extensions.Logging;
using Silk.NET.OpenGL;
using System.Numerics;

namespace Acme.Render.GL {
    using GL = Silk.NET.OpenGL.GL;
    public unsafe class GLSLShader : IShader {
        private readonly OpenGLGraphicsDevice _device;
        private readonly Dictionary<string, int> _uniformLocations = [];
        private readonly ILogger _log;
        private GL GL => _device.GL;

        public uint Program { get; protected set; }
        public uint ProgramId => Program;
        public string Name { get; }
        public bool NeedsLoad { get; protected set; }

        public GLSLShader(OpenGLGraphicsDevice device, string name, string vertSource, string fragSource, ILogger log) {
            _device = device;
            Name = name;
            _log = log;
            Load(vertSource, fragSource);
        }

        public GLSLShader(OpenGLGraphicsDevice device, string name, string shaderDirectory, ILogger log) {
            _device = device;
            Name = name;
            _log = log;
            var vertPath = Path.Combine(shaderDirectory, name + ".vert");
            var fragPath = Path.Combine(shaderDirectory, name + ".frag");
            Load(File.ReadAllText(vertPath), File.ReadAllText(fragPath));
        }

        private int GetUniformLocation(uint program, string name) {
            if (!_uniformLocations.ContainsKey(name)) {
                _uniformLocations.Add(name, GL.GetUniformLocation(program, name));
            }
            return _uniformLocations[name];
        }

        public void SetUniform(string location, Matrix4x4 m) {
            var m2 = new float[] {
                m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43,
                m.M44
            };
            fixed (float* transform = m2) {
                GL.UniformMatrix4(GetUniformLocation(Program, location), 1, false, transform);
            }
        }

        public void SetUniform(string location, int v) {
            GL.Uniform1(GetUniformLocation(Program, location), v);
        }

        public void SetUniform(string location, Vector2 vec) {
            GL.Uniform2(GetUniformLocation(Program, location), vec);
        }

        public void SetUniform(string location, Vector3 vec) {
            GL.Uniform3(GetUniformLocation(Program, location), vec);
        }

        public void SetUniform(string location, Vector3[] vecs) {
            fixed (float* v = &vecs[0].X) {
                GL.Uniform3(GetUniformLocation(Program, location), 3, v);
            }
        }

        public void SetUniform(string location, Vector4 vec) {
            GL.Uniform4(GetUniformLocation(Program, location), vec);
        }

        public void SetUniform(string location, float v) {
            GL.Uniform1(GetUniformLocation(Program, location), v);
        }

        public void SetUniform(string location, float[] vs) {
            fixed (float* v = &vs[0]) {
                GL.Uniform1(GetUniformLocation(Program, location), (uint)vs.Length, v);
            }
        }

        public void Load(string vertShaderSource, string fragShaderSource) {
            if (string.IsNullOrWhiteSpace(vertShaderSource) || string.IsNullOrWhiteSpace(fragShaderSource)) {
                _log.LogError($"Shader {Name} has no source code!");
                return;
            }

            uint vertexShader = CompileShader(ShaderType.VertexShader, Name, vertShaderSource);
            uint fragmentShader = CompileShader(ShaderType.FragmentShader, Name, fragShaderSource);

            if (vertexShader == 0 || fragmentShader == 0) {
                _log.LogError($"Shader {Name}: Failed to compile vertex or fragment shader");
                return;
            }

            var prog = GL.CreateProgram();
            GLHelpers.CheckErrors(true);
            GL.AttachShader(prog, vertexShader);
            GLHelpers.CheckErrors(true);
            GL.AttachShader(prog, fragmentShader);
            GLHelpers.CheckErrors(true);
            GL.LinkProgram(prog);
            GLHelpers.CheckErrors(true);

            GL.GetProgram(prog, GLEnum.LinkStatus, out int success);
            GLHelpers.CheckErrors();
            if (success != 1) {
                var infoLog = GL.GetProgramInfoLog(prog);
                _log.LogError($"Error: shader program {Name} linking failed: {infoLog}");
                GL.DeleteShader(vertexShader);
                GL.DeleteShader(fragmentShader);
                GL.DeleteProgram(prog);
                return;
            }

            GL.DeleteShader(vertexShader);
            GLHelpers.CheckErrors();
            GL.DeleteShader(fragmentShader);
            GLHelpers.CheckErrors();

            if (Program != 0) {
                Unload();
            }

            _uniformLocations.Clear();
            Program = prog;
            NeedsLoad = false;
        }

        private uint CompileShader(ShaderType shaderType, string name, string shaderSource) {
            uint shader = GL.CreateShader(shaderType);
            GLHelpers.CheckErrors();
            GL.ShaderSource(shader, shaderSource);
            GLHelpers.CheckErrors();
            GL.CompileShader(shader);
            GLHelpers.CheckErrors();

            GL.GetShader(shader, ShaderParameterName.CompileStatus, out int success);
            GLHelpers.CheckErrors();
            if (success != 1) {
                var infoLog = GL.GetShaderInfoLog(shader);
                _log.LogError($"Error: {name}:{shaderType} compilation failed: {infoLog}");
                GL.DeleteShader(shader);
                return 0;
            }
            return shader;
        }

        public void Bind() {
            if (Program == 0) {
                _log.LogError($"Bind called on shader {Name} but Program is 0!");
                return;
            }
            GL.UseProgram(Program);
            var err = GL.GetError();
            if (err != GLEnum.NoError) {
                _log.LogError($"UseProgram({Program}) for shader {Name} failed with error: {err}");
            }
        }

        public void Unbind() {
            GL.UseProgram(0);
        }

        protected void Unload() {
            if (Program != 0) {
                GL.DeleteProgram(Program);
                GLHelpers.CheckErrors();
                Program = 0;
            }
        }

        public void Dispose() {
            Unload();
        }
    }
}
