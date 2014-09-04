﻿using eBay.Service.Core.Soap;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BerkeleyEntities;

namespace EbayServices.Mappers
{
    public class HandbagAdapter : ProductMapper
    {
        public HandbagAdapter(Item item) : base(item)
        {

        }

        public override List<NameValueListType> GetItemSpecifics()
        {
            List<NameValueListType> nameValueList = new List<NameValueListType>();

            nameValueList.Add(BuildItemSpecific("Brand", new string[1] { this.ToTitleCase(_item.SubDescription1) }));

            if (!string.IsNullOrWhiteSpace(_item.GTIN))
            {
                nameValueList.Add(BuildItemSpecific("UPC", new string[1] { _item.GTIN }));
            }

            if (_item.Category != null)
            {
                nameValueList.Add(BuildItemSpecific("Style", new string[1] { _item.Category.Name }));
            }

            if (!string.IsNullOrWhiteSpace(_item.SubDescription2))
            {
                nameValueList.Add(BuildItemSpecific("Color", new string[1] { this.ToTitleCase(_item.SubDescription2) }));
            }

            return nameValueList;
        }

        public override List<NameValueListType> GetVariationSpecifics()
        {
            return new List<NameValueListType>();
        }
    }
}
