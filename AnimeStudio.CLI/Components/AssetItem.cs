namespace AnimeStudio.CLI
{
    public class AssetItem
    {
        public string Text;
        public Object Asset;
        public SerializedFile SourceFile;
        public string Container = string.Empty;
        public string TypeString;
        public long m_PathID;
        public long FullSize;
        public ClassIDType Type;
        public string InfoText;
        public string UniqueID;
        public string LibraryRole;
        public bool DiagnosticOnly;
        public string SourcePartRole;
        public string VisualAcceptanceScope;
        public string CoveredByPrefabContainer;
        public string CoveredByPrefabSourceObjectKey;
        public string LibraryRoleReason;

        public AssetItem(Object asset)
        {
            Asset = asset;
            Text = asset.Name;
            SourceFile = asset.assetsFile;
            Type = asset.type;
            TypeString = Type.ToString();
            m_PathID = asset.m_PathID;
            FullSize = asset.byteSize;
        }
    }
}
