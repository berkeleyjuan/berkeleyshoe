﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BerkeleyEntities;
using eBay.Service.Core.Soap;
using System.IO;
using System.Net;
using eBay.Service.Call;
using eBay.Service.Core.Sdk;
using EbayServices.Mappers;
using EbayServices.Services;
using System.Xml;
using System.Data;
using System.Threading.Tasks;

namespace BerkeleyEntities.Ebay
{
    public delegate void PublishingResultHandler(ResultArgs e);

    public class Publisher
    {
        public const string FORMAT_FIXEDPRICE = "FixedPriceItem";
        public const string FORMAT_AUCTION = "Chinese";
        public const string STATUS_ACTIVE = "Active";

        private berkeleyEntities _dataContext;
        private ListingMapper _listingMapper;
        private EbayMarketplace _marketplace;
        private ListingSyncService _listingSyncService;

        public Publisher(berkeleyEntities dataContext,  EbayMarketplace marketplace)
        {
            _marketplace = marketplace;
            _dataContext = dataContext;

            _listingSyncService = new ListingSyncService(marketplace.ID);
            _listingMapper = new ListingMapper(_dataContext, _marketplace);
        }

        public event PublishingResultHandler Result;

        public void Publish()
        {
            var modified = _dataContext.ObjectStateManager.GetObjectStateEntries(EntityState.Modified).Select(p => p.Entity).OfType<EbayListing>().ToList();
            var created = _dataContext.ObjectStateManager.GetObjectStateEntries(EntityState.Added).Select(p => p.Entity).OfType<EbayListing>().ToList();

            foreach (var listing in created.Concat(modified))
            {
                try
                {
                    switch (listing.EntityState)
                    {
                        case EntityState.Added:
                            PublishListing(listing);
                            break;

                        case EntityState.Modified:
                            if (listing.ListingItems.All(p => p.Quantity == 0))
                                EndListing(listing);
                            else
                                ReviseListing(listing);
                            break;
                    }

                    PersistListing(listing);

                    if (this.Result != null)
                    {
                        this.Result(new ResultArgs() { Listing = listing, Message = string.Empty, IsError = false });
                    }
                }
                catch (ApiException e)
                {
                    var errors = e.Errors.ToArray();

                    if (errors.Any(p => p.ErrorCode.Equals("518")))
                    {
                        throw;
                    }

                    if (this.Result != null)
                    {
                        this.Result(new ResultArgs() { Listing = listing, Message = e.Message, IsError = true });
                    }
                }
                catch (Exception e)
                {
                    if (this.Result != null)
                    {
                        this.Result(new ResultArgs() { Listing = listing, Message = e.Message, IsError = true });
                    }
                }
            }
        }

        private void PersistListing(EbayListing listing)
        {
            using (berkeleyEntities dataContext = new berkeleyEntities())
            {
                var newListing = dataContext.CopyEntity<EbayListing>(listing,true);

                dataContext.EbayListings.AddObject(newListing);

                foreach (var listingItem in listing.ListingItems)
                {
                    newListing.ListingItems.Add(dataContext.CopyEntity<EbayListingItem>(listingItem, true));
                }

                foreach (var relation in listing.Relations)
                {
                    var newRelation = dataContext.CopyEntity<EbayPictureUrlRelation>(relation, true);

                    newRelation.Listing = newListing;
                    newRelation.PictureServiceUrl = dataContext.CopyEntity<EbayPictureServiceUrl>(relation.PictureServiceUrl, true);
                }

                dataContext.SaveChanges();
            }
        }

        public void Revert(EbayListing listing)
        {
            foreach (EbayListingItem listingItem in listing.ListingItems.ToList())
            {
                _dataContext.EbayListingItems.Detach(listingItem);
            }

            foreach (EbayPictureUrlRelation relation in listing.Relations.ToList())
            {
                _dataContext.EbayPictureUrlRelations.Detach(relation);
            }

            _dataContext.EbayListings.Detach(listing);
        }

        private void EndListing(EbayListing listing)
        {
            EndItemRequestType request = new EndItemRequestType();
            request.ItemID = listing.Code;
            request.EndingReasonSpecified = true;
            request.EndingReason = EndReasonCodeType.NotAvailable;
                           
            EndItemCall call = new EndItemCall(_marketplace.GetApiContext());

            EndItemResponseType response = call.ExecuteRequest(request) as EndItemResponseType;
        
            listing.Status = "Completed";

            if (response.EndTimeSpecified)
            {
                listing.EndTime = response.EndTime;
            }
        }

        private void PublishListing(EbayListing listing)
        {
             UploadPictures(listing);

            if ((bool)listing.IsVariation)
            {
                AddFixedPriceItemRequestType request = new AddFixedPriceItemRequestType();
                request.Item = _listingMapper.Map(listing);

                AddFixedPriceItemCall call = new AddFixedPriceItemCall(_marketplace.GetApiContext());
                AddFixedPriceItemResponseType response = call.ExecuteRequest(request) as AddFixedPriceItemResponseType;
             
                listing.LastSyncTime = DateTime.UtcNow;
                listing.Code = response.ItemID;
                listing.StartTime = response.StartTime;
                listing.EndTime = response.EndTime;
                listing.Status = "Active";
            }
            else
            {
                AddItemRequestType request = new AddItemRequestType();
                request.Item = _listingMapper.Map(listing);

                AddItemCall call = new AddItemCall(_marketplace.GetApiContext());
                AddItemResponseType response = call.ExecuteRequest(request) as AddItemResponseType;
                listing.LastSyncTime = DateTime.UtcNow;
                listing.Code = response.ItemID;
                listing.StartTime = response.StartTime;
                listing.EndTime = response.EndTime;
                listing.Status = "Active";
            }

            
        }

        private void ReviseListing(EbayListing listing)
        {
            if ((bool)listing.IsVariation)
            {
                ReviseFixedPriceItemRequestType request = new ReviseFixedPriceItemRequestType();
                request.Item = _listingMapper.Map(listing);

                ReviseFixedPriceItemCall call = new ReviseFixedPriceItemCall(_marketplace.GetApiContext());
                ReviseFixedPriceItemResponseType response = call.ExecuteRequest(request) as ReviseFixedPriceItemResponseType;
                listing.LastSyncTime = DateTime.UtcNow;
                
            }
            else
            {
                ReviseItemRequestType request = new ReviseItemRequestType();
                request.Item = _listingMapper.Map(listing);

                
                ReviseItemCall call = new ReviseItemCall(_marketplace.GetApiContext());
                ReviseItemResponseType response = call.ExecuteRequest(request) as ReviseItemResponseType;
                listing.LastSyncTime = DateTime.UtcNow;
                
            }
        }

        private void UploadPictures(EbayListing listing)
        {
            var pendingUrls = listing.Relations.Select(p => p.PictureServiceUrl).Where(p => p.Url == null).ToList();

            var tasks = new List<Task>();

            foreach (var urlData in pendingUrls)
            {
                tasks.Add(Task.Run(() => UploadSiteHostedPictures(urlData)));
            }

            Task.WaitAll(tasks.ToArray());
        }

        private void UploadSiteHostedPictures(EbayPictureServiceUrl urlData)
        {
            string boundary = "MIME_boundary";
            string CRLF = "\r\n";

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create("https://api.ebay.com/ws/api.dll");
            request.Headers.Add("X-EBAY-API-COMPATIBILITY-LEVEL", "515");
            request.Headers.Add("X-EBAY-API-DEV-NAME", "2ec7ed97-e88e-4868-aeb8-9be51f107a46"); //use your devid
            request.Headers.Add("X-EBAY-API-APP-NAME", "Mecalzoc-a8e6-4947-ac5b-49be352cd2f4"); //use your appid
            request.Headers.Add("X-EBAY-API-CERT-NAME", "Mecalzoc-a8e6-4947-ac5b-49be352cd2f4"); //use your certid
            request.Headers.Add("X-EBAY-API-SITEID", "0");
            request.Headers.Add("X-EBAY-API-DETAIL-LEVEL", "0");
            request.Headers.Add("X-EBAY-API-CALL-NAME", "UploadSiteHostedPictures");
            request.ContentType = "multipart/form-data; boundary=" + boundary;
            request.ProtocolVersion = HttpVersion.Version10;
            request.ContentType = "multipart/form-data; boundary=" + boundary;
            request.Method = "POST";

            string payload1 = "--" + boundary + CRLF +
                "Content-Disposition: form-data; name=document" + CRLF +
                "Content-Type: text/xml; charset=\"UTF-8\"" + CRLF + CRLF +
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<UploadSiteHostedPicturesRequest xmlns=\"urn:ebay:apis:eBLBaseComponents\">" +
                "<PictureSet>Supersize</PictureSet>" +
                "<RequesterCredentials><eBayAuthToken>" + _marketplace.Token + "</eBayAuthToken></RequesterCredentials>" +
                "</UploadSiteHostedPicturesRequest>" +
                CRLF + "--" + boundary + CRLF +
                "Content-Disposition: form-data; name=image; filename=image" + CRLF +
                "Content-Type: application/octet-stream" + CRLF +
                "Content-Transfer-Encoding: binary" + CRLF + CRLF;

            string payload3 = CRLF + "--" + boundary + "--" + CRLF;

            byte[] postDataBytes1 = Encoding.ASCII.GetBytes(payload1);
            byte[] postDataBytes2 = Encoding.ASCII.GetBytes(payload3);
            byte[] image = null;

            using (Stream fileStream = new FileStream(urlData.Path, FileMode.Open, FileAccess.Read))
            {
                fileStream.Seek(0, SeekOrigin.Begin);

                using (BinaryReader reader = new BinaryReader(fileStream))
                {
                    image = reader.ReadBytes((int)fileStream.Length);
                }
            }

            int length = postDataBytes1.Length + image.Length + postDataBytes2.Length;

            request.ContentLength = length;

            using (Stream stream = request.GetRequestStream())
            {
                byte[] bytes = postDataBytes1.Concat(image).Concat(postDataBytes2).ToArray();

                stream.Write(bytes, 0, length);
            }

            HttpWebResponse response = (HttpWebResponse)request.GetResponse();

            string output = null;

            using (StreamReader sr = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                output = sr.ReadToEnd();
            }

            XmlDocument xmlResponse = new XmlDocument();

            xmlResponse.LoadXml(output);

            XmlNodeList list = xmlResponse.GetElementsByTagName("FullURL", "urn:ebay:apis:eBLBaseComponents");

            if (list[0] == null)
            {
                list = xmlResponse.GetElementsByTagName("Errors");
                ErrorType error = new ErrorType();
                foreach (XmlNode node in list[0])
                {
                    switch (node.Name)
                    {
                        case "ShortMessage": error.ShortMessage = node.InnerText; break;
                        case "LongMessage": error.LongMessage = node.InnerText; break;
                        case "ErrorCode": error.ErrorCode = node.InnerText; break;
                    }
                }

                throw new ApiException(new ErrorTypeCollection(new ErrorType[1] { error }));
            }

            urlData.Url = list[0].InnerText;
            urlData.TimeUploaded = DateTime.UtcNow;
        }
    }

    public class ResultArgs
    {
        public bool IsError { get; set; }

        public string Message {get; set;}

        public EbayListing Listing {get; set;}
    }
}
