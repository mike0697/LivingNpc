using System;
using System.Collections.Generic;

namespace LivingNPC.Maps
{
    [Serializable]
    public class LivingNPCMapProject
    {
        public string format;
        public int version;
        public string name;
        public string startNode;
        public List<LivingNPCMapProjectNode> nodes = new();
    }

    [Serializable]
    public class LivingNPCMapProjectNode
    {
        public string id;
        public LivingNPCGraphPosition graph;
    }

    [Serializable]
    public class LivingNPCGraphPosition
    {
        public float x;
        public float y;
    }

    [Serializable]
    public class LivingNPCMapNode
    {
        public string id;
        public string title;
        public string text;
        public Dictionary<string, string> backgrounds = new();
        public List<LivingNPCMapLink> links = new();
    }

    [Serializable]
    public class LivingNPCMapLink
    {
        public string id;
        public string target;
        public string label;
        public float x;
        public float y;
        public float width = 18f;
        public float height = 6.5f;
    }
}
