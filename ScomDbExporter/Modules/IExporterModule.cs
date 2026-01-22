namespace ScomDbExporter.Modules
{
    internal interface IExporterModule
    {
        string Name { get; }
        bool Enabled { get; }

        /// Called once at startup.
        void Init();

        /// Called repeatedly from main loop.
        void Tick();
    }
}
