using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace DeezFiles.Models
{
    public class LocalFilesInfo
    {
        public string Name { get; set; }
        public string Date { get; set; }
        public int Size { get; set; }
        public SHA256 Hash { get; set; }
    }
}
