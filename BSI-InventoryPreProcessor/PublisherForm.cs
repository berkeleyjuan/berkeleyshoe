﻿//
// A la Mayor Gloria a Dios
//


using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.IO;
using System.Reflection;
using System.Configuration;

using System.Data;
using System.Data.Sql;
using System.Data.SqlClient;


using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using EbayServices;
using AmazonServices;
using BerkeleyEntities;



namespace BSI_InventoryPreProcessor
{
    public partial class PublisherForm : Form
    {

        private EbayMarketplace _marketplace;
        private ExcelWorkbook _workbook;

        public PublisherForm()
        {
            InitializeComponent();            
        } 

        private void btnStart_Click(object sender, EventArgs e)
        {
            
        } 

        private void btnSetWorkbook_Click(object sender, EventArgs e)
        {
            OpenFileDialog ofd = new OpenFileDialog();
            ofd.Filter = "Excel Files|*.xlsx";
            DialogResult dr = ofd.ShowDialog();


            if (dr.Equals(DialogResult.OK) && !string.IsNullOrWhiteSpace(ofd.FileName))
            {
                _workbook = new ExcelWorkbook(ofd.FileName);
                _workbook.FetchEntries();
                cbMarketplaces.DataSource = _workbook.EbayEntries.Keys;
                lbCurrentWorkbook.Text = ofd.FileName;
            }
        }

        private void btnPublish_Click(object sender, EventArgs e)
        {

            string marketplaceCode = cbMarketplaces.SelectedText;

            if (string.IsNullOrWhiteSpace(marketplaceCode))
            {
                MessageBox.Show("No marketplace selected");
                return;
            }

            btnPublish.Enabled = false;

            using (berkeleyEntities dataContext = new berkeleyEntities())
            {
                if (dataContext.EbayMarketplaces.Any(p => p.Code.Equals(marketplaceCode)))
                {
                    EbayMarketplace marketplace = dataContext.EbayMarketplaces.Single(p => p.Code.Equals(marketplaceCode));

                    EbayPublisher publisher = new EbayPublisher(marketplace.ID, _workbook.EbayEntries[marketplaceCode]);

                    TabPage tab = new TabPage();
                    tab.Text = publisher.Header;
                    tab.Controls.Add(new DataGridView() {  DataSource = publisher.Entries });
                    tcMarketplaceSheet.TabPages.Add(tab);
                    publisher.PublishAsync();

                }
                else if (dataContext.AmznMarketplaces.Any(p => p.Code.Equals(marketplaceCode)))
                {
                    AmznMarketplace marketplace = dataContext.AmznMarketplaces.Single(p => p.Code.Equals(marketplaceCode));

                    AmznPublisher publisher = new AmznPublisher(marketplace.ID, _workbook.AmznEntries[marketplace.Code]);

                    TabPage tab = new TabPage();
                    tab.Text = publisher.Header;
                    tab.Controls.Add(new DataGridView() { DataSource = publisher.Entries });
                    tcMarketplaceSheet.TabPages.Add(tab);
                    publisher.PublishAsync();
                }


            }
        } 

    }
}