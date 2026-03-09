namespace RoseEngine
{
    /// <summary>
    /// Unity-compatible Shader class.
    /// Represents a named shader program that determines how a Material is rendered.
    /// </summary>
    public class Shader
    {
        public string name { get; }

        internal Shader(string name)
        {
            this.name = name;
        }

        /// <summary>
        /// Finds a shader by name. Supported built-in shaders:
        /// - "Skybox/Panoramic" : Equirectangular panoramic skybox
        /// - "Skybox/Procedural" : Procedural atmospheric sky
        /// </summary>
        public static Shader? Find(string name)
        {
            return name switch
            {
                "Skybox/Panoramic" => new Shader(name),
                "Skybox/Procedural" => new Shader(name),
                _ => null,
            };
        }
    }
}
