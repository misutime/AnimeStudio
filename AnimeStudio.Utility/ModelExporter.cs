namespace AnimeStudio
{
    public static class ModelExporter
    {
        public static void ExportFbx(string path, IImported imported, Fbx.ExportOptions exportOptions)
        {
            Fbx.Exporter.Export(path, imported, exportOptions);
        }

        public static void ExportGltf(string path, IImported imported, Gltf.ExportOptions exportOptions)
        {
            Gltf.Exporter.Export(path, imported, exportOptions);
        }
    }
}
