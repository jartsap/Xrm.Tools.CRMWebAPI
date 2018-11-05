using System;
using System.Collections.Generic;
using System.Text;

namespace Xrm.Tools.WebAPI.Results
{
    public class CRMGetListFetchXmlResult<ListType>
    {
        public List<ListType> List { get; set; }

        public int Count { get; set; }
        public int Total { get; set; }

        public string PagingCookie { get; set; }

        
    }
}
