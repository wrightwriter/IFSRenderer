﻿using System;
using System.Collections.Generic;
using System.Text;

namespace IFSEngine.TransformFunctions
{
    public class Loonie : ITransformFunction
    {
        public string ShaderCode => throw new NotImplementedException();

        public int Id => 4;

        public List<double> GetListOfParams()
        {
            return new List<double>();//0
        }
    }
}