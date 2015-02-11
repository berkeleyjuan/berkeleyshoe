﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BerkeleyEntities;
using BerkeleyEntities.Amazon;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Input;
using Microsoft.TeamFoundation.MVVM;
using WorkbookPublisher.View;
using System.ComponentModel;
using System.Windows.Data;
using System.Collections.ObjectModel;
using WorkbookPublisher.ViewModel;
using AmazonServices.Mappers;
using System.Windows;
using System.Data;

namespace WorkbookPublisher.ViewModel
{
    
    public class AmznPublisherViewModel : PublisherViewModel
    {
        public AmznPublisherViewModel(ExcelWorkbook workbook, string marketplaceCode) : base(workbook, marketplaceCode)
        {
            _readEntriesCommand = new AmznReadCommand(_workbook, marketplaceCode);
            _publishCommand = new AmznPublishCommand(marketplaceCode);
            _updateCommand = new UpdateCommand(_workbook, marketplaceCode);

            _readEntriesCommand.ReadCompleted += _publishCommand.ReadCompletedHandler;
            _readEntriesCommand.ReadCompleted += _updateCommand.ReadCompletedHandler;
            _updateCommand.FixCompleted += _readEntriesCommand.FixCompletedHandler;
            _updateCommand.FixCompleted += _publishCommand.FixCompletedHandler;
        }
    }

    public class AmznReadCommand : ReadCommand
    {
        public AmznMarketplace _marketplace;
        private uint _lastRowIndex;
        private List<ListingEntry> _newEntries;

        public AmznReadCommand(ExcelWorkbook workbook, string marketplaceCode)
            : base(workbook, marketplaceCode)
        {

        }

        public override List<ListingEntry> UpdateAndValidateEntries(List<ListingEntry> entries)
        {
            _lastRowIndex = entries.Max(p => p.RowIndex);
            _newEntries = new List<ListingEntry>();

            var entryGroups = entries.Where(p => p.Status.Equals(StatusCode.Pending)).Cast<AmznEntry>().GroupBy(p => p.Sku);

            using (berkeleyEntities dataContext = new berkeleyEntities())
            {
                _marketplace = dataContext.AmznMarketplaces.Single(p => p.Code.Equals(_marketplaceCode));

                foreach (var group in entryGroups)
                {
                    Item item = dataContext.Items.SingleOrDefault(p => p.ItemLookupCode.Equals(group.Key));

                    if (item != null)
                    {
                        UpdateGroup(item, group.ToList());

                        foreach (var entry in group)
                        {
                            entry.ClassName = item.ClassName;
                            entry.Brand = item.SubDescription1;

                            if (entry.Status.Equals(StatusCode.Pending))
                            {
                                Validate(item, entry);
                            }
                        }
                    }
                    else
                    {
                        foreach (var entry in group)
                        {
                            entry.Message = "sku not found";
                            entry.Status = StatusCode.Error;
                        }
                    }
                } 
            }

            return _newEntries;
        }

        private void Validate(Item item, AmznEntry entry)
        {
            if (item.Notes != null)
            {
                if (item.Notes.Contains("PRE") || item.Notes.Contains("NWB") || item.Notes.Contains("NWD"))
                {
                    entry.Message = "only new products allowed";
                    entry.Status = StatusCode.Error;
                }
            }

            if (entry.Q > item.QtyAvailable)
            {
                entry.Message = "out of stock";
                entry.Status = StatusCode.Error;
            }

            if (string.IsNullOrEmpty(item.GTIN))
            {
                entry.Message = "UPC or EAN required";
                entry.Status = StatusCode.Error;
            }

            if (item.Department == null)
            {
                entry.Message = "department classification required";
                entry.Status = StatusCode.Error;
            }
        }

        private void UpdateGroup(Item item, IEnumerable<AmznEntry> group)
        {
            var active = item.AmznListingItems.Where(p => p.IsActive && p.MarketplaceID == _marketplace.ID);

            var existingEntries = group.ToList();

            foreach (var listingItem in active)
            {
                var entry = existingEntries.FirstOrDefault();

                if (entry != null)
                {
                    if (listingItem.Quantity == entry.Q && decimal.Compare(listingItem.Price, entry.P) == 0 && entry.GetUpdateFlags().Count == 0)
                    {
                        entry.Status = StatusCode.Completed;
                    }
                    else
                    {
                        entry.Message = string.Format("({0}|{1})", listingItem.Quantity, Math.Round(listingItem.Price, 2));
                    }

                    existingEntries.Remove(entry);
                }
                else
                {
                    entry = new AmznEntry();
                    entry.Sku = item.ItemLookupCode;
                    entry.Brand = item.SubDescription1;
                    entry.ClassName = item.ClassName;

                    entry.P = listingItem.Price;
                    entry.Q = listingItem.Quantity;
                    entry.Title = listingItem.Title;
                    entry.Message = "added by program";
                    entry.Status = StatusCode.Completed;

                    entry.ParentRowIndex = group.First().RowIndex;
                    entry.RowIndex = _lastRowIndex + 1;

                    _lastRowIndex = entry.RowIndex;
                    _newEntries.Add(entry);
                }
                
            }
        }

        public override Type EntryType
        {
            get { return typeof(AmznEntry); }
        }
    }

    public class AmznPublishCommand : PublishCommand
    {
        private berkeleyEntities _dataContext = new berkeleyEntities();
        private AmznMarketplace _marketplace;

        public List<AmznEntry> _processingEntries = new List<AmznEntry>();
        private Queue<IEnumerable<PublishingResult>> _pendingResubmission = new Queue<IEnumerable<PublishingResult>>();

        public AmznPublishCommand(string marketplaceCode)
        {
            _marketplace = _dataContext.AmznMarketplaces.Single(p => p.Code.Equals(marketplaceCode));
        }

        private void Publisher_Result(List<PublishingResult> results)
        {
            foreach (var result in results)
            {
                AmznEntry entry = _processingEntries.Single(p => p.Sku.Equals(result.Data.Sku));

                entry.ClearMessages();

                if (result.HasError)
                {
                    entry.Status = StatusCode.Error;
                    entry.Message = result.Message;
                }
                else
                {
                    entry.Status = StatusCode.Completed;
                    entry.Command = string.Empty;
                }
            }

            var productErrors = results.Where(p => p.HasError && p.Data.ProductData != null);

            if (productErrors.Count() > 0)
            {
                _pendingResubmission.Enqueue(productErrors); 
            }
        }

        public async override void Publish(IEnumerable<ListingEntry> entries)
        {
            _processingEntries.AddRange(entries.Cast<AmznEntry>());

            List<ListingItemDto> listingItems = new List<ListingItemDto>();

            foreach (AmznEntry entry in entries)
            {
                ListingItemDto listingItem = new ListingItemDto();

                listingItem.Sku = entry.Sku;
                listingItem.Title = entry.Title;

                var existingListingItem = _dataContext.AmznListingItems.SingleOrDefault(p => p.IsActive && p.Item.ItemLookupCode.Equals(entry.Sku) && p.MarketplaceID == _marketplace.ID);

                listingItem.IncludeProductData = existingListingItem == null || entry.GetUpdateFlags().Any(p => p.Equals("PRODUCTDATA"));

                listingItem.Qty = entry.Q;
                listingItem.QtySpecified = existingListingItem == null || existingListingItem.Quantity != entry.Q;

                listingItem.Price = entry.P;
                listingItem.PriceSpecified = existingListingItem == null || decimal.Compare(existingListingItem.Price, entry.P) != 0;

                listingItems.Add(listingItem);

                entry.Status = StatusCode.Processing;
            }

            BerkeleyEntities.Amazon.AmazonServices services = new BerkeleyEntities.Amazon.AmazonServices();

            services.Result += Publisher_Result;

            await Task.Run(() => services.Publish(_marketplace.ID, listingItems));

            while (_pendingResubmission.Count > 0)
            {
                var results = _pendingResubmission.Dequeue();

                RepublishDataWindow republishForm = new RepublishDataWindow();
                republishForm.DataContext = CollectionViewSource.GetDefaultView(results);
                republishForm.ShowDialog();

                if ((bool)republishForm.DialogResult)
                {
                    foreach (var result in results)
                    {
                        AmznEntry entry = _processingEntries.Single(p => p.Sku.Equals(result.Data.Sku));
                        entry.Status = StatusCode.Processing;
                    }

                    await Task.Run(() => services.Publish(_marketplace.ID, results.Select(p => p.Data)));
                }
            }

            this.RaisePublishCompleted();
        }

    }

}
