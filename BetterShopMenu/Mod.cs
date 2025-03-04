using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BetterShopMenu.Framework;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Pathoschild.Stardew.ChestsAnywhere;
using SpaceShared;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Locations;
using StardewValley.Menus;
using tlitookilakin.HDPortraits;
using SObject = StardewValley.Object;
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable once ClassNeverInstantiated.Global
// ReSharper disable RedundantAssignment

namespace BetterShopMenu
{
    /// <summary>The mod entry point.</summary>
    internal class Mod : StardewModdingAPI.Mod
    {

        public const int UnitWidth = 170;
        public const int UnitHeight = 144;
        public const int UnitsHigh = 3;
        public const int UnitsWide = 6; //(Shop.width - 32) / UnitWidth
        private const int SeedsOtherCategory = -174; //seeds - 100;
        private const int PurchaseCountdownStart = 60 * 600 / 1000; //600ms
        private const int PurchaseCountdownRepeat = 60 * 100 / 1000; //100ms
        public static Mod Instance;
        public static Configuration Config;
        internal ClickableTextureComponent ActiveButton;
        private List<int> Categories;
        private Dictionary<int, string> CategoryNames;
        internal bool ChestsAnywhereActive;
        internal IChestsAnywhereApi ChestsAnywhereApi;
        private Dictionary<int, string> CropData;
        private int CurrCategory;
        internal bool CustomBackpackFramework;
        private bool FirstTick;
        internal ClickableTextureComponent GridClickableButton;
        internal bool GridLayoutActive;
        private bool HasRecipes;

        private bool HaveStockList;
        internal IHDPortraitsAPI HdPortraitsApi;
        private List<ISalable> InitialItems;
        private Dictionary<ISalable, ItemStockInformation> InitialStock;

        internal ClickableTextureComponent LinearClickableButton;
        private int MaxQuantityValue;
        private IClickableMenu NumberQuantityMenu;
        private int PurchaseCountdown;

        private Point PurchasePoint;

        internal int QuantityIndex;
        private IReflectedField<TemporaryAnimatedSpriteList> ReflectAnimations;
        private IReflectedField<string> ReflectBoldTitleText;
        private IReflectedField<int> ReflectHoverPrice;
        private IReflectedField<string> ReflectHoverText;
        private IReflectedField<bool> ReflectIsStorageShop;
        private IReflectedField<TemporaryAnimatedSprite> ReflectPoof;

        private IReflectedField<Rectangle> ReflectScrollBarRunner;
        private IReflectedField<float> ReflectSellPercentage;
        private IReflectedMethod ReflectTryToPurchaseItem;
        private bool RightClickDown;
        private TextBox Search;

        internal ShopMenu Shop;
        private int Sorting;

        public override void Entry(IModHelper helper)
        {
            I18n.Init(helper.Translation);

            Mod.Instance = this;
            Log.Monitor = Monitor;
            Mod.Config = helper.ReadConfig<Configuration>();

            GridClickableButton = new ClickableTextureComponent(Rectangle.Empty,
                helper.ModContent.Load<Texture2D>("assets/buttonGrid.png"),
                new Rectangle(0, 0, 16, 16),
                4f);
            LinearClickableButton = new ClickableTextureComponent(Rectangle.Empty,
                helper.ModContent.Load<Texture2D>("assets/buttonStd.png"),
                new Rectangle(0, 0, 16, 16),
                4f);
            GridLayoutActive = Mod.Config.GridLayout;
            ActiveButton = GridLayoutActive ? LinearClickableButton : GridClickableButton;

            NumberQuantityMenu = null;

            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.Display.MenuChanged += OnMenuChanged;
            //helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            //helper.Events.Display.RenderedActiveMenu += this.OnRenderedActiveMenu;
            //helper.Events.Input.ButtonPressed += this.OnButtonPressed;
            //helper.Events.Input.ButtonReleased += this.OnButtonReleased;
            //helper.Events.Input.MouseWheelScrolled += this.OnMouseWheelScrolled;

            var harmony = new Harmony(ModManifest.UniqueID);
            // ReSharper disable once JoinDeclarationAndInitializer
            // ReSharper disable once NotAccessedVariable
            MethodInfo mInfo;

            // these patches only patch the source method out when the grid layout is enabled

            // this patches out the ShopMenu mouse wheel code.
            mInfo = harmony.Patch(AccessTools.Method(typeof(ShopMenu), nameof(ShopMenu.receiveScrollWheelAction)),
                new HarmonyMethod(typeof(ShopMenuPatches), nameof(ShopMenuPatches.ShopMenu_receiveScrollWheelAction_Prefix))
                );

            // this patches the ShopMenu performHoverAction code.
            // we block the ShopMenu code from the grid layout area. otherwise we allow it. e.g. inventory menu.
            mInfo = harmony.Patch(AccessTools.Method(typeof(ShopMenu), nameof(ShopMenu.performHoverAction)),
                new HarmonyMethod(typeof(ShopMenuPatches), nameof(ShopMenuPatches.ShopMenu_performHoverAction_Prefix))
                );

            // this patches out the ShopMenu mouse right click code.
            // this allows us to trigger a delay for doing the right click, hold auto purchase.
            // SMAPI input suppression (preferred) gets in the way of detecting a hold.
            mInfo = harmony.Patch(AccessTools.Method(typeof(ShopMenu), nameof(ShopMenu.receiveRightClick)),
                new HarmonyMethod(typeof(ShopMenuPatches), nameof(ShopMenuPatches.ShopMenu_receiveRightClick_Prefix))
                );

            // this patches out ShopMenu.draw.
            // excluding the grid layout draw, our draw procedure is really mostly a copy of much the Stardew ShopMenu.draw code.
            Type[] drawParams =
            {
                typeof(SpriteBatch)
            };
            mInfo = harmony.Patch(AccessTools.Method(typeof(ShopMenu), nameof(ShopMenu.draw), drawParams),
                new HarmonyMethod(typeof(ShopMenuPatches), nameof(ShopMenuPatches.ShopMenu_draw_Prefix))
                );
        }

        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            var configMenu = Helper.ModRegistry.GetGenericModConfigMenuApi(Monitor);
            if (configMenu != null)
            {
                configMenu.Register(
                    ModManifest,
                    () => Mod.Config = new Configuration(),
                    () => Helper.WriteConfig(Mod.Config)
                    );
                configMenu.AddBoolOption(
                    ModManifest,
                    name: I18n.Config_GridLayout_Name,
                    tooltip: I18n.Config_GridLayout_Tooltip,
                    getValue: () => Mod.Config.GridLayout,
                    setValue: value => Mod.Config.GridLayout = value);
                configMenu.AddBoolOption(
                    ModManifest,
                    name: I18n.Config_QuantityDialog_Name,
                    tooltip: I18n.Config_QuantityDialog_Tooltip,
                    getValue: () => Mod.Config.QuantityDialog,
                    setValue: value => Mod.Config.QuantityDialog = value
                    );
            }

            ChestsAnywhereActive = false;
            ChestsAnywhereApi = Helper.ModRegistry.GetApi<IChestsAnywhereApi>("Pathoschild.ChestsAnywhere");

            CustomBackpackFramework = Helper.ModRegistry.IsLoaded("aedenthorn.CustomBackpack");
            //Log.Debug($"CustomBackpackFramework = {this.CustomBackpackFramework}");

            HdPortraitsApi = Helper.ModRegistry.GetApi<IHDPortraitsAPI>("tlitookilakin.HDPortraits");
            //this.HdPortraitsApi = null;
        }

        private void InitShop(ShopMenu shopMenu)
        {
            Shop = shopMenu;
            FirstTick = true;

            Helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            Helper.Events.Display.RenderedActiveMenu += OnRenderedActiveMenu;
            Helper.Events.Input.ButtonPressed += OnButtonPressed;
            Helper.Events.Input.ButtonReleased += OnButtonReleased;
            Helper.Events.Input.MouseWheelScrolled += OnMouseWheelScrolled;

            ReflectScrollBarRunner = Helper.Reflection.GetField<Rectangle>(shopMenu, "scrollBarRunner");
            ReflectAnimations = Helper.Reflection.GetField<TemporaryAnimatedSpriteList>(shopMenu, "animations");
            ReflectPoof = Helper.Reflection.GetField<TemporaryAnimatedSprite>(shopMenu, "poof");
            ReflectIsStorageShop = Helper.Reflection.GetField<bool>(shopMenu, "_isStorageShop");
            ReflectSellPercentage = Helper.Reflection.GetField<float>(shopMenu, "sellPercentage");
            ReflectHoverText = Helper.Reflection.GetField<string>(shopMenu, "hoverText");
            ReflectBoldTitleText = Helper.Reflection.GetField<string>(shopMenu, "boldTitleText");
            ReflectHoverPrice = Helper.Reflection.GetField<int>(shopMenu, "hoverPrice");
            ReflectTryToPurchaseItem = Helper.Reflection.GetMethod(shopMenu, "tryToPurchaseItem");

            GridLayoutActive = Mod.Config.GridLayout;

            var bounds = new Rectangle(shopMenu.xPositionOnScreen - 48, shopMenu.yPositionOnScreen + 530, 64, 64);
            LinearClickableButton.bounds = bounds;
            GridClickableButton.bounds = bounds;

            ChestsAnywhereActive = ChestsAnywhereApi != null && ChestsAnywhereApi.IsOverlayActive();

            NumberQuantityMenu = null;
            QuantityIndex = -1;
        }

        private void InitShop2()
        {
            FirstTick = false;

            InitialItems = Shop.forSale;
            InitialStock = Shop.itemPriceAndStock;

            CropData = null;
            HaveStockList = Game1.MasterPlayer.hasOrWillReceiveMail("PierreStocklist");
            if (HaveStockList)
                CropData = Game1.content.Load<Dictionary<int, string>>("Data\\Crops");

            RightClickDown = false;
            PurchaseCountdown = -1;

            Categories = new List<int>();
            HasRecipes = false;
            foreach (var salable in InitialItems)
            {
                var item = salable as Item;
                var obj = item as SObject;
                int sCat = item?.Category ?? 0;

                if (!Categories.Contains(sCat) && obj is not { IsRecipe: true })
                    Categories.Add(sCat);

                if (sCat == SObject.SeedsCategory && HaveStockList && !Categories.Contains(Mod.SeedsOtherCategory))
                    Categories.Add(Mod.SeedsOtherCategory);

                if (obj?.IsRecipe == true)
                    HasRecipes = true;
            }
            CurrCategory = -1;

            CategoryNames = new Dictionary<int, string>
            {
                [-1] = I18n.Categories_Everything(), [0] = I18n.Categories_Other(), [SObject.GreensCategory] = I18n.Categories_Greens(), [SObject.GemCategory] = I18n.Categories_Gems(),
                [SObject.VegetableCategory] = I18n.Categories_Vegetables(), [SObject.FishCategory] = I18n.Categories_Fish(), [SObject.EggCategory] = I18n.Categories_Eggs(), [SObject.MilkCategory] = I18n.Categories_Milk(),
                [SObject.CookingCategory] = I18n.Categories_Cooking(), [SObject.CraftingCategory] = I18n.Categories_Crafting(), [SObject.BigCraftableCategory] = I18n.Categories_BigCraftables(), [SObject.FruitsCategory] = I18n.Categories_Fruits(),
                [SObject.SeedsCategory] = I18n.Categories_Seeds(), [Mod.SeedsOtherCategory] = I18n.Categories_SeedsOther(), [SObject.mineralsCategory] = I18n.Categories_Minerals(), [SObject.flowersCategory] = I18n.Categories_Flowers(),
                [SObject.meatCategory] = I18n.Categories_Meat(), [SObject.metalResources] = I18n.Categories_Metals(), [SObject.buildingResources] = I18n.Categories_BuildingResources(), //?
                [SObject.sellAtPierres] = I18n.Categories_SellToPierre(), [SObject.sellAtPierresAndMarnies] = I18n.Categories_SellToPierreOrMarnie(), [SObject.fertilizerCategory] = I18n.Categories_Fertilizer(), [SObject.junkCategory] = I18n.Categories_Junk(),
                [SObject.baitCategory] = I18n.Categories_Bait(), [SObject.tackleCategory] = I18n.Categories_Tackle(), [SObject.sellAtFishShopCategory] = I18n.Categories_SellToWilly(), [SObject.furnitureCategory] = I18n.Categories_Furniture(),
                [SObject.ingredientsCategory] = I18n.Categories_Ingredients(), [SObject.artisanGoodsCategory] = I18n.Categories_ArtisanGoods(), [SObject.syrupCategory] = I18n.Categories_Syrups(), [SObject.monsterLootCategory] = I18n.Categories_MonsterLoot(),
                [SObject.equipmentCategory] = I18n.Categories_Equipment(), [SObject.hatCategory] = I18n.Categories_Hats(), [SObject.ringCategory] = I18n.Categories_Rings(), [SObject.weaponCategory] = I18n.Categories_Weapons(),
                [SObject.bootsCategory] = I18n.Categories_Boots(), [SObject.toolCategory] = I18n.Categories_Tools(), [SObject.clothingCategory] = I18n.Categories_Clothing(), [Categories.Count == 0 ? 1 : Categories.Count] = I18n.Categories_Recipes()
            };

            Search = new TextBox(Game1.content.Load<Texture2D>("LooseSprites\\textBox"), null, Game1.smallFont, Game1.textColor);

            SyncStock();
        }

        private void ChangeCategory(int amt)
        {
            CurrCategory += amt;

            if (CurrCategory == -2)
                CurrCategory = HasRecipes ? Categories.Count : Categories.Count - 1;
            if (CurrCategory == Categories.Count && !HasRecipes || CurrCategory > Categories.Count)
                CurrCategory = -1;

            SyncStock();
        }
        private void ChangeSorting(int amt)
        {
            Sorting += amt;
            Sorting = Sorting switch
            {
                > 2 => 0,
                < 0 => 2,
                _ => Sorting
            };

            SyncStock();
        }

        private bool SeedsFilter(ISalable item, bool inSeason)
        {
            if (!HaveStockList || item is not Item thisItem)
                return true;
            int seedIndex = thisItem.ParentSheetIndex;

            if (!CropData.ContainsKey(seedIndex))
                return inSeason; //have this stuff show in the in season list. saplings are like this.
            string[] split = CropData[seedIndex].Split('/');
            return split[1].Contains(Game1.currentSeason, StringComparison.OrdinalIgnoreCase) == inSeason;
        }

        private bool ItemMatchesCategory(ISalable item, int cat)
        {
            var obj = item as SObject;
            if (cat == -1)
                return true;
            if (cat == Categories.Count)
                return obj?.IsRecipe == true;
            if (Categories[cat] == Mod.SeedsOtherCategory && item is Item { Category: SObject.SeedsCategory })
                return true;
            if (Categories[cat] == ((item as Item)?.Category ?? 0))
                return obj is not { IsRecipe: true };
            return false;
        }

        private void SyncStock()
        {

            Shop.currentItemIndex = 0;

            int curCat = CurrCategory;
            int sCat = 0;
            bool inSeason = true;
            if (curCat >= 0 && curCat < Categories.Count)
            {
                sCat = Categories[curCat];
                inSeason = sCat == SObject.SeedsCategory;
            }

            var items =
                InitialItems
                    .Where(item => ItemMatchesCategory(item, curCat) && (Search.Text == null || item.DisplayName.Contains(Search.Text, StringComparison.CurrentCultureIgnoreCase)))
                    .Where(item => curCat < 0 || sCat != SObject.SeedsCategory && sCat != Mod.SeedsOtherCategory || SeedsFilter(item, inSeason))
                    .ToList();

            var stock =
                InitialStock
                    .Where(item => ItemMatchesCategory(item.Key, curCat) && (Search.Text == null || item.Key.DisplayName.Contains(Search.Text, StringComparison.CurrentCultureIgnoreCase)))
                    .Where(item => curCat < 0 || sCat != SObject.SeedsCategory && sCat != Mod.SeedsOtherCategory || SeedsFilter(item.Key, inSeason))
                    .ToDictionary(item => item.Key, item => item.Value);

            Shop.forSale = items;
            Shop.itemPriceAndStock = stock;

            DoSorting();
        }
        private void DoSorting()
        {
            var items = Shop.forSale;
            var stock = Shop.itemPriceAndStock;
            if (Sorting == 0)
                return;
            switch (Sorting)
            {
                case 1:
                    items.Sort((a, b) => stock[a].Price - stock[b].Price);
                    break;
                case 2:
                    items.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCulture));
                    break;
            }
        }

        /// <summary>
        ///     When a menu is open (<see cref="Game1.activeClickableMenu" /> isn't null), raised after that menu is drawn to
        ///     the sprite batch but before it's rendered to the screen.
        /// </summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void OnRenderedActiveMenu(object sender, RenderedActiveMenuEventArgs e)
        {
            if (Shop == null)
                return;
            //if (Game1.activeClickableMenu != this.Shop)
            //{
            //    Log.Debug($"OnRenderedActiveMenu Game1.activeClickableMenu != shop. {Game1.activeClickableMenu}");
            //    return;
            //}

            bool background = NumberQuantityMenu != null || ChestsAnywhereActive && ChestsAnywhereApi.IsOverlayModal();

            if (GridLayoutActive)
                DrawGridLayout(e.SpriteBatch, background);
            else
                DrawNewFields(e.SpriteBatch);

            NumberQuantityMenu?.draw(e.SpriteBatch);

            Shop.drawMouse(e.SpriteBatch);
        }

        private void DrawNewFields(SpriteBatch b)
        {
            var pos = new Vector2(Shop.xPositionOnScreen + 25, Shop.yPositionOnScreen + 525);
            IClickableMenu.drawTextureBox(b, (int)pos.X, (int)pos.Y, 200, 72, Color.White);
            pos.X += 16;
            pos.Y += 16;
            string str = $"{I18n.Filter_Category()}\n" + CategoryNames[CurrCategory == -1 || CurrCategory == Categories.Count ? CurrCategory : Categories[CurrCategory]];
            b.DrawString(Game1.dialogueFont, str, pos + new Vector2(-1, 1), new Color(224, 150, 80), 0, Vector2.Zero, 0.5f, SpriteEffects.None, 0);
            b.DrawString(Game1.dialogueFont, str, pos, new Color(86, 22, 12), 0, Vector2.Zero, 0.5f, SpriteEffects.None, 0);

            pos = new Vector2(Shop.xPositionOnScreen + 25, Shop.yPositionOnScreen + 600);
            IClickableMenu.drawTextureBox(b, (int)pos.X, (int)pos.Y, 200, 48, Color.White);
            pos.X += 16;
            pos.Y += 16;
            str = I18n.Filter_Sorting() + " " + (Sorting == 0 ? I18n.Sort_None() : Sorting == 1 ? I18n.Sort_Price() : I18n.Sort_Name());
            b.DrawString(Game1.dialogueFont, str, pos + new Vector2(-1, 1), new Color(224, 150, 80), 0, Vector2.Zero, 0.5f, SpriteEffects.None, 0);
            b.DrawString(Game1.dialogueFont, str, pos, new Color(86, 22, 12), 0, Vector2.Zero, 0.5f, SpriteEffects.None, 0);

            pos.X = Shop.xPositionOnScreen + 25;
            pos.Y = Shop.yPositionOnScreen + 650;
            //e.SpriteBatch.DrawString( Game1.dialogueFont, "Search: ", pos, Game1.textColor, 0, Vector2.Zero, 0.5f, SpriteEffects.None, 0 );
            Search.X = (int)pos.X; // + Game1.dialogueFont.MeasureString( "Search: " ).X);
            Search.Y = (int)pos.Y;
            Search.Draw(b);

            ActiveButton.draw(b);
            if (ActiveButton.bounds.Contains(Game1.getOldMouseX(true), Game1.getOldMouseY(true)))
                IClickableMenu.drawHoverText(b,
                    GridLayoutActive ? I18n.Button_StdLayout_Tooltip() : I18n.Button_GridLayout_Tooltip(),
                    Game1.smallFont);
        }

        private void DrawGridLayout(SpriteBatch b, bool background)
        {
            var shop = Shop;
            var forSale = shop.forSale;
            var itemPriceAndStock = shop.itemPriceAndStock;
            int currency = shop.currency;
            var animations = ReflectAnimations.GetValue();
            var poof = ReflectPoof.GetValue();
            var heldItem = shop.heldItem;
            int currentItemIndex = shop.currentItemIndex;
            var scrollBar = shop.scrollBar;
            var scrollBarRunner = ReflectScrollBarRunner.GetValue();
            ISalable hover = null;

            if (!Game1.options.showMenuBackground)
                b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.75f);

            var purchaseTexture = Game1.mouseCursors;
            var purchaseWindowBorder = new Rectangle(384, 373, 18, 18);
            var purchaseItemRect = new Rectangle(384, 396, 15, 15);
            int purchaseItemTextColor = -1;
            var purchaseSelectedColor = Color.Wheat;
            if (shop.ShopId == "QiGemShop")
            {
                purchaseTexture = Game1.mouseCursors2;
                purchaseWindowBorder = new Rectangle(0, 256, 18, 18);
                purchaseItemRect = new Rectangle(18, 256, 15, 15);
                purchaseItemTextColor = 4;
                purchaseSelectedColor = Color.Blue;
            }

            //IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 373, 18, 18), shop.xPositionOnScreen, shop.yPositionOnScreen, shop.width, shop.height - 256 + 32 + 4, Color.White, 4f, true);
            IClickableMenu.drawTextureBox(b, purchaseTexture, purchaseWindowBorder, shop.xPositionOnScreen, shop.yPositionOnScreen, shop.width, shop.height - 256 + 32 + 4, Color.White, 4f);
            for (int i = currentItemIndex * Mod.UnitsWide; i < forSale.Count && i < currentItemIndex * Mod.UnitsWide + Mod.UnitsWide * 3; ++i)
            {
                bool failedCanPurchaseCheck = shop.canPurchaseCheck != null && !shop.canPurchaseCheck(i);
                int ix = i % Mod.UnitsWide;
                int iy = i / Mod.UnitsWide;

                var rect = new Rectangle(shop.xPositionOnScreen + 16 + ix * Mod.UnitWidth,
                    shop.yPositionOnScreen + 16 + iy * Mod.UnitHeight - currentItemIndex * Mod.UnitHeight,
                    Mod.UnitWidth, Mod.UnitHeight);
                bool selectedItem = rect.Contains(Game1.getOldMouseX(true), Game1.getOldMouseY(true));
                IClickableMenu.drawTextureBox(b, purchaseTexture, purchaseItemRect, rect.X, rect.Y, rect.Width, rect.Height, selectedItem ? purchaseSelectedColor : Color.White, 4f, false);

                var item = forSale[i];
                if (selectedItem)
                    hover = item;

                StackDrawType stackDrawType;
                if (shop.ShopId == "QiGemShop")
                    stackDrawType = StackDrawType.HideButShowQuality;
                else if (shop.itemPriceAndStock[item].Stock == int.MaxValue)
                    stackDrawType = StackDrawType.HideButShowQuality;
                else
                {
                    stackDrawType = StackDrawType.Draw_OneInclusive;
                    if (ReflectIsStorageShop.GetValue())
                        stackDrawType = StackDrawType.Draw;
                }
                if (forSale[i].ShouldDrawIcon())
                    item.drawInMenu(b, new Vector2(rect.X + 48, rect.Y + 16), 1f, 1, 1, stackDrawType, Color.White, true);
                int price = itemPriceAndStock[forSale[i]].Price;
                string priceStr = price.ToString();
                if (price > 0)
                {
                    if (price < 1000000)
                        SpriteText.drawString(b,
                            priceStr,
                            rect.X + (rect.Width - SpriteText.getWidthOfString(priceStr)) / 2, //rect.Right - SpriteText.getWidthOfString(priceStr) - 16,
                            rect.Y + 80,
                            alpha: ShopMenu.getPlayerCurrencyAmount(Game1.player, currency) >= price && !failedCanPurchaseCheck ? 1f : 0.5f,
                            color: shop.VisualTheme.ItemRowTextColor);
                    else
                    {
                        // SpriteText font is too big/long. this is about all I can do. we lose the alpha ability.
                        var font = Game1.dialogueFont;

                        Utility.drawTextWithShadow(b,
                            priceStr,
                            font,
                            new Vector2(rect.X + (rect.Width - font.MeasureString(priceStr).X) / 2, rect.Y + 80),
                            purchaseItemTextColor == -1 ? new Color(86, 22, 12) : Color.White);
                        //Utility.drawBoldText(b,
                        //                     priceStr,
                        //                     font,
                        //                     new Vector2(rect.X + ((rect.Width - font.MeasureString(priceStr).X) / 2), rect.Y + 80),
                        //                     purchaseItemTextColor == -1 ? new Color(86, 22, 12) : Color.White);
                    }
                    //Utility.drawWithShadow(b, Game1.mouseCursors, new Vector2(rect.Right - 16, rect.Y + 80), new Rectangle(193 + currency * 9, 373, 9, 10), Color.White, 0, Vector2.Zero, 1, layerDepth: 1);
                }
                else if (itemPriceAndStock[forSale[i]].TradeItem != null)
                {
                    int requiredItemCount = 5;
                    string requiredItem = itemPriceAndStock[forSale[i]].TradeItem;
                    if (itemPriceAndStock[forSale[i]].TradeItemCount != null)
                        requiredItemCount = itemPriceAndStock[forSale[i]].TradeItemCount.Value;
                    bool hasEnoughToTrade = Game1.player.Items.ContainsId(requiredItem, requiredItemCount);
                    if (shop.canPurchaseCheck != null && !shop.canPurchaseCheck(i))
                        hasEnoughToTrade = false;
                    float textWidth = SpriteText.getWidthOfString("x" + requiredItemCount);
                    Utility.drawWithShadow(b,
                        Game1.objectSpriteSheet,
                        new Vector2(rect.Right - 64 - textWidth, rect.Y + 80 - 4),
                        Game1.getSourceRectForStandardTileSheet(Game1.objectSpriteSheet, requiredItem.Length, 16, 16),
                        Color.White * (hasEnoughToTrade ? 1f : 0.25f),
                        0f,
                        Vector2.Zero,
                        3, false,
                        -1f, -1, -1,
                        hasEnoughToTrade ? 0.35f : 0f);
                    SpriteText.drawString(b,
                        "x" + requiredItemCount,
                        rect.Right - (int)textWidth - 16, rect.Y + 80, 999999,
                        -1, 999999, hasEnoughToTrade ? 1f : 0.5f, 0.88f, false, -1, "", shop.VisualTheme.ItemRowTextColor);
                }
            }
            if (forSale.Count == 0)
                SpriteText.drawString(b, Game1.content.LoadString("Strings\\StringsFromCSFiles:ShopMenu.cs.11583"), shop.xPositionOnScreen + shop.width / 2 - SpriteText.getWidthOfString(Game1.content.LoadString("Strings\\StringsFromCSFiles:ShopMenu.cs.11583")) / 2, shop.yPositionOnScreen + shop.height / 2 - 128);

            shop.drawCurrency(b);
            //if (currency == 0)
            //    Game1.dayTimeMoneyBox.drawMoneyBox(b, shop.xPositionOnScreen - 36, shop.yPositionOnScreen + shop.height - shop.inventory.height + 48);

            // background for the inventory menu
            // support the bigger backpack mod
            int biggerPack = shop.inventory.capacity > 36 ? 64 : 0;
            IClickableMenu.drawTextureBox(b,
                Game1.mouseCursors,
                new Rectangle(384, 373, 18, 18),
                shop.xPositionOnScreen + shop.width - shop.inventory.width - 32 - 24,
                shop.yPositionOnScreen + shop.height - 256 + 40,
                shop.inventory.width + 56,
                shop.height - 448 + 20 + biggerPack,
                Color.White, 4f);

            shop.inventory.draw(b);

            for (int index = animations.Count - 1; index >= 0; --index)
                if (animations[index].update(Game1.currentGameTime))
                    animations.RemoveAt(index);
                else
                    animations[index].draw(b, true);
            poof?.draw(b);

            // dressers, furniture catalog, floor/wallpaper catalog, have tabs
            foreach (var t in shop.tabButtons)
                t.draw(b);

            shop.upperRightCloseButton.draw(b);
            shop.upArrow.draw(b);
            shop.downArrow.draw(b);
            if (forSale.Count > Mod.UnitsWide * Mod.UnitsHigh)
            {
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6), scrollBarRunner.X, scrollBarRunner.Y, scrollBarRunner.Width, scrollBarRunner.Height, Color.White, 4f);
                scrollBar.draw(b);
            }

            int portraitDrawPosition = shop.xPositionOnScreen - 320;
            if (portraitDrawPosition > 0 && Game1.options.showMerchantPortraits)
            {
                if (shop.portraitTexture != null)
                {
                    Utility.drawWithShadow(b, Game1.mouseCursors, new Vector2(portraitDrawPosition, shop.yPositionOnScreen), new Rectangle(603, 414, 74, 74), Color.White, 0f, Vector2.Zero, 4f, false, 0.91f);
                    if (shop.portraitTexture != null)
                    {
                        if (HdPortraitsApi == null)
                            b.Draw(shop.portraitTexture, new Vector2(portraitDrawPosition + 20, shop.yPositionOnScreen + 20), new Rectangle(0, 0, 64, 64), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.92f);
                        else
                            HdPortraitsApi.DrawPortrait(b, (NPC)shop.source, NPC.portrait_neutral_index, new Point(portraitDrawPosition + 20, shop.yPositionOnScreen + 20), Color.White);
                    }
                }
                if (shop.potraitPersonDialogue != null && !background)
                {
                    portraitDrawPosition = shop.xPositionOnScreen - (int)Game1.dialogueFont.MeasureString(shop.potraitPersonDialogue).X - 64;
                    if (portraitDrawPosition > 0)
                        IClickableMenu.drawHoverText(b, shop.potraitPersonDialogue, Game1.dialogueFont, 0, 0, -1, null, -1, null, null, 0, null, -1, portraitDrawPosition, shop.yPositionOnScreen + (shop.portraitTexture != null ? 312 : 0));
                }
            }

            DrawNewFields(b); // we want hover text to cover our new fields

            if (background)
                return;
            shop.hoveredItem = hover; // lookup anything mod examines the hoveredItem field. maybe others.

            if (hover != null)
            {
                // get hover price & stock
                if (itemPriceAndStock == null || !itemPriceAndStock.TryGetValue(hover, out var hoverPriceAndStock))
                    hoverPriceAndStock = new ItemStockInformation(-1, -1);

                // render tooltip
                string hoverText = hover.getDescription();
                string boldTitleText = hover.DisplayName;
                int hoverPrice = hoverPriceAndStock.Price == -1 ? hover.salePrice() : hoverPriceAndStock.Price;
                string getHoveredItemExtraItem = null;
                if (hoverPriceAndStock.TradeItem != null)
                    getHoveredItemExtraItem = hoverPriceAndStock.TradeItem;
                int getHoveredItemExtraItemAmount = 5;
                if (hoverPriceAndStock.TradeItemCount != null)
                    getHoveredItemExtraItemAmount = hoverPriceAndStock.TradeItemCount.Value;
                IClickableMenu.drawToolTip(b, hoverText, boldTitleText, hover as Item, heldItem != null, -1, currency, getHoveredItemExtraItem, getHoveredItemExtraItemAmount, null, hoverPrice);
            }
            else
            {
                // the inventory may have created some hover text (via ShopMenu.performHoverAction).
                // typically this is an item that can be sold to the vendor. other times clothing gets a hover.
                int price = ReflectHoverPrice.GetValue();
                string hoverText = ReflectHoverText.GetValue();
                string boldTitleText = ReflectBoldTitleText.GetValue();
                if (!hoverText.Equals(""))
                    IClickableMenu.drawToolTip(b, hoverText, boldTitleText, null,
                        currencySymbol: currency,
                        moneyAmountToShowAtBottom: price);
            }

            heldItem?.drawInMenu(b, new Vector2(Game1.getOldMouseX(true) + 8, Game1.getOldMouseY(true) + 8), 1f, 1f, 0.9f, StackDrawType.Draw, Color.White, true);
        }

        private void CloseQuantityDialog(bool cancel)
        {
            int amount = 0;
            int idx = QuantityIndex;

            if (!cancel)
            {
                var textBox = Helper.Reflection.GetField<TextBox>(NumberQuantityMenu, "numberSelectedBox").GetValue();
                if (!int.TryParse(textBox.Text, out amount))
                    amount = 0;

                if (amount > 999)
                    amount = 999;
            }

            QuantityIndex = -1;
            Shop.SetChildMenu(null);
            NumberQuantityMenu = null;

            //call the purchase code here
            if (amount > 0 && idx >= 0)
                PurchaseItem(amount, idx);
        }

        private void BehaviorOnNumberSelect(int number, int price, Farmer who)
        {
            //unused. should probably dump this.
        }

        private void CreateQuantityDialog()
        {
            var shop = Shop;
            var forSale = shop.forSale;
            var itemPriceAndStock = shop.itemPriceAndStock;

            int idx = QuantityIndex;
            int price = -1;
            if (itemPriceAndStock[forSale[idx]].Price > 0)
                price = itemPriceAndStock[forSale[idx]].Price;

            int max = Math.Min(999, ShopMenu.getPlayerCurrencyAmount(Game1.player, shop.currency) / Math.Max(1, itemPriceAndStock[forSale[idx]].Price));
            MaxQuantityValue = max;

            NumberQuantityMenu = new NumberSelectionMenu(I18n.Quantity_Name(), BehaviorOnNumberSelect, price, 0, max, 1);
            Shop.SetChildMenu(NumberQuantityMenu);
        }

        private bool GetQuantityIndex()
        {
            var shop = Shop;
            var forSale = shop.forSale;

            for (int i = 0; i < forSale.Count; i++)
            {
                if (forSale[i] != shop.hoveredItem)
                    continue;
                QuantityIndex = i;
                return true;
            }
            return false;
        }

        /// <summary>Raised after the player presses a button on the keyboard, controller, or mouse.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void OnButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            var shop = Shop;
            
            if (shop == null)
                return;
            if (Game1.activeClickableMenu != shop)
                //Log.Debug($"OnButtonPressed Game1.activeClickableMenu != shop. {Game1.activeClickableMenu}");
                return;
            if (ChestsAnywhereActive && ChestsAnywhereApi.IsOverlayModal())
                return; // Chests Anywhere's options / dropdown view is handling input

            if (NumberQuantityMenu != null)
            {
                var nMenu = NumberQuantityMenu as NumberSelectionMenu;

                var uiCursor = Utility.ModifyCoordinatesForUIScale(e.Cursor.ScreenPixels);
                int x = (int)uiCursor.X;
                int y = (int)uiCursor.Y;

                if (nMenu != null && (nMenu.okButton.containsPoint(x, y) && e.Button is SButton.MouseLeft || e.Button is SButton.Enter))
                {
                    var currentValue = Helper.Reflection.GetField<int>(NumberQuantityMenu, "currentValue");
                    if (currentValue.GetValue() <= MaxQuantityValue)
                    {
                        Game1.playSound("smallSelect");
                        Helper.Input.Suppress(e.Button);
                        CloseQuantityDialog(false);
                    }
                    else
                    {
                        var shake = Helper.Reflection.GetField<int>(NumberQuantityMenu, "priceShake");
                        shake.SetValue(2000);
                        Helper.Input.Suppress(e.Button);
                        Game1.playSound("bigDeSelect");
                    }
                }
                else if (nMenu != null && (nMenu.cancelButton.containsPoint(x, y) && e.Button is SButton.MouseLeft || e.Button is SButton.Escape))
                {
                    Game1.playSound("bigDeSelect");
                    Helper.Input.Suppress(e.Button);
                    CloseQuantityDialog(true);
                }
            }
            else if (e.Button is SButton.MouseLeft or SButton.MouseRight or SButton.ControllerA or SButton.ControllerX)
            {
                var uiCursor = Utility.ModifyCoordinatesForUIScale(e.Cursor.ScreenPixels);
                int x = (int)uiCursor.X;
                int y = (int)uiCursor.Y;
                int direction = e.Button == SButton.MouseLeft ? 1 : -1;

                var categoryRect = new Rectangle(shop.xPositionOnScreen + 25, shop.yPositionOnScreen + 525, 200, 72);
                var sortRect = new Rectangle(shop.xPositionOnScreen + 25, shop.yPositionOnScreen + 600, 200, 48);
                //var menuRect = new Rectangle(shop.xPositionOnScreen, shop.yPositionOnScreen, shop.width, shop.height - 256 + 32 + 4);

                if (categoryRect.Contains(x, y))
                    ChangeCategory(direction);
                else if (sortRect.Contains(x, y))
                    ChangeSorting(direction);
                else if (e.Button == SButton.MouseLeft && ActiveButton.bounds.Contains(x, y))
                {
                    GridLayoutActive = !GridLayoutActive;
                    ActiveButton = GridLayoutActive ? LinearClickableButton : GridClickableButton;
                    Shop.currentItemIndex = 0;
                }
                else if (
                    Mod.Config.QuantityDialog &&
                    e.Button == SButton.MouseRight &&
                    e.IsDown(SButton.LeftAlt) &&
                    shop.hoveredItem != null &&
                    GetQuantityIndex() &&
                    Shop.forSale[QuantityIndex].maximumStackSize() > 1
                    )
                {
                    Helper.Input.Suppress(e.Button);
                    CreateQuantityDialog();
                }
                else if (GridLayoutActive)
                {
                    var pt = new Point(x, y);
                    if (e.Button == SButton.MouseRight)
                    {
                        // the mouse state is always released if we suppress input via SMAPI.
                        // the suppression causes an immediate mouse up when you suppress a mouse down.
                        // this gets in the way of detecting a mouse button down hold. e.g. shop menu repeat purchase feature.
                        //this.Helper.Input.Suppress(e.Button); suppressed via Harmony
                        RightClickDown = true;
                        DoGridLayoutRightClick(e, pt);
                    }
                    else
                    {
                        if (DoGridLayoutLeftClick(e, pt))
                            Helper.Input.Suppress(e.Button);
                    }
                }
            }
            //else if ((e.Button is (>= SButton.A and <= SButton.Z) or SButton.Space or SButton.Back) && this.Search.Selected)
            // sync on any search box input. not just simple ascii A…Z.
            else if (Search.Selected)
            {
                Helper.Input.Suppress(e.Button);
                SyncStock();
            }
        }

        /// <summary>Raised after the player releases a button on the keyboard, controller, or mouse.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void OnButtonReleased(object sender, ButtonReleasedEventArgs e)
        {
            if (e.Button != SButton.MouseRight)
                return;
            RightClickDown = false;
            PurchaseCountdown = -1;
        }

        /// <summary>Raised after the game state is updated (≈60 times per second).</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (Shop == null)
                return;
            if (FirstTick)
                InitShop2();
            else if (Game1.activeClickableMenu != Shop)
                //Log.Debug($"OnUpdateTicked Game1.activeClickableMenu != shop. {Game1.activeClickableMenu}");
                return;
            else if (ChestsAnywhereActive && ChestsAnywhereApi.IsOverlayModal())
                return; // Chests Anywhere's options / dropdown view is handling input

            bool oldMode = Game1.uiMode;
            Game1.uiMode = true;
            Search.Update();
            Game1.uiMode = oldMode;

            if (!GridLayoutActive || !RightClickDown || PurchaseCountdown <= 0)
                return;
            PurchaseCountdown--;
            if (PurchaseCountdown != 0)
                return;
            if (Game1.input.GetMouseState().RightButton == ButtonState.Pressed)
                DoGridLayoutRightClick(null, PurchasePoint);
        }

        private void CloseShopMenu()
        {
            if (Shop == null)
                return;
            Log.Trace("Closing shop menu.");
            Shop = null;
            CropData = null;

            if (Search != null)
            {
                Search.Selected = false;
                Search = null;
            }

            QuantityIndex = -1;
            NumberQuantityMenu = null;

            Helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
            Helper.Events.Display.RenderedActiveMenu -= OnRenderedActiveMenu;
            Helper.Events.Input.ButtonPressed -= OnButtonPressed;
            Helper.Events.Input.ButtonReleased -= OnButtonReleased;
            Helper.Events.Input.MouseWheelScrolled -= OnMouseWheelScrolled;

            ReflectScrollBarRunner = null;
            ReflectAnimations = null;
            ReflectPoof = null;
            ReflectIsStorageShop = null;
            ReflectSellPercentage = null;
            ReflectHoverText = null;
            ReflectBoldTitleText = null;
            ReflectHoverPrice = null;
            ReflectTryToPurchaseItem = null;
        }

        /// <summary>Raised after a game menu is opened, closed, or replaced.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void OnMenuChanged(object sender, MenuChangedEventArgs e)
        {
            if (e.OldMenu is ShopMenu oldMenu)
                if (oldMenu == Shop)
                    CloseShopMenu();

            if (e.NewMenu is ShopMenu shopMenu)
            {
                Log.Trace("Found new shop menu!");
                InitShop(shopMenu);
            }
            else
            {
                // oldMenu above should catch a close, but just do this as a safety net.
                if (Shop != null)
                    CloseShopMenu();
            }
        }

        private void DoScroll(int direction)
        {
            var forSale = Shop.forSale;
            int currentItemIndex = Shop.currentItemIndex;
            var scrollBar = Shop.scrollBar;
            var scrollBarRunner = ReflectScrollBarRunner.GetValue();
            var downArrow = Shop.downArrow;
            var upArrow = Shop.upArrow;
            int rows = forSale.Count / Mod.UnitsWide;
            if (forSale.Count % Mod.UnitsWide != 0)
                rows++;
            int rowsH = rows - Mod.UnitsHigh; //this may go negative. that's okay.

            switch (direction)
            {
                case < 0:
                    {
                        if (currentItemIndex < rowsH)
                        {
                            downArrow.scale = downArrow.baseScale;
                            Shop.currentItemIndex = currentItemIndex += 1;
                            if (forSale.Count > 0)
                            {
                                scrollBar.bounds.Y = scrollBarRunner.Height / Math.Max(1, rowsH) * currentItemIndex + upArrow.bounds.Bottom + 4;
                                if (currentItemIndex >= rowsH)
                                    scrollBar.bounds.Y = downArrow.bounds.Y - scrollBar.bounds.Height - 4;
                            }
                            Game1.playSound("shwip");
                        }
                        break;
                    }
                case > 0:
                    {
                        if (currentItemIndex > 0)
                        {
                            upArrow.scale = upArrow.baseScale;
                            Shop.currentItemIndex = currentItemIndex -= 1;
                            if (forSale.Count > 0)
                            {
                                scrollBar.bounds.Y = scrollBarRunner.Height / Math.Max(1, rowsH) * currentItemIndex + upArrow.bounds.Bottom + 4;
                                if (currentItemIndex >= rowsH)
                                    scrollBar.bounds.Y = downArrow.bounds.Y - scrollBar.bounds.Height - 4;
                            }
                            Game1.playSound("shwip");
                        }
                        break;
                    }
            }
        }

        private void OnMouseWheelScrolled(object sender, MouseWheelScrolledEventArgs e)
        {
            if (Shop == null || !GridLayoutActive)
                return;
            // we only scroll if the mouse is not over the inventory when the Custom Backpack Framework mod is installed.
            // it will want to scroll the inventory as necessary.
            var uiCursor = Utility.ModifyCoordinatesForUIScale(e.Position.ScreenPixels);
            if (!CustomBackpackFramework || !Shop.inventory.isWithinBounds((int)uiCursor.X, (int)uiCursor.Y))
                DoScroll(e.Delta);
        }

        private void PurchaseItem(int numberToBuy, int idx)
        {
            var shop = Shop;
            var forSale = shop.forSale;
            var itemPriceAndStock = shop.itemPriceAndStock;
            if (idx < 0)
                return;

            numberToBuy = Math.Min(
                Math.Min(numberToBuy, ShopMenu.getPlayerCurrencyAmount(Game1.player, shop.currency) / Math.Max(1, itemPriceAndStock[forSale[idx]].Price)),
                Math.Max(1, itemPriceAndStock[forSale[idx]].Stock)
                );
            numberToBuy = Math.Min(numberToBuy, forSale[idx].maximumStackSize());

            if (numberToBuy == -1)
                numberToBuy = 1;

            switch (numberToBuy)
            {
                //tryToPurchase may change heldItem.
                case > 0 when ReflectTryToPurchaseItem.Invoke<bool>(forSale[idx], shop.heldItem, numberToBuy, PurchasePoint.X, PurchasePoint.Y):
                    itemPriceAndStock.Remove(forSale[idx]);
                    forSale.RemoveAt(idx);
                    break;
                case <= 0:
                    Game1.dayTimeMoneyBox.moneyShakeTimer = 1000;
                    Game1.playSound("cancel");
                    break;
            }

            if (shop.heldItem == null || !ReflectIsStorageShop.GetValue() && !Game1.options.SnappyMenus || Game1.activeClickableMenu is not ShopMenu || !Game1.player.addItemToInventoryBool(shop.heldItem as Item))
                return;
            shop.heldItem = null;
            DelayedAction.playSoundAfterDelay("coin", 100);
        }

        private bool DoGridLayoutLeftClick(ButtonPressedEventArgs e, Point pt)
        {
            var shop = Shop;
            var forSale = shop.forSale;
            int currentItemIndex = shop.currentItemIndex;
            var animations = ReflectAnimations.GetValue();
            float sellPercentage = ReflectSellPercentage.GetValue();
            var scrollBarRunner = ReflectScrollBarRunner.GetValue();
            var scrollBar = shop.scrollBar;
            var downArrow = shop.downArrow;
            var upArrow = shop.upArrow;
            int rows = forSale.Count / Mod.UnitsWide;
            if (forSale.Count % Mod.UnitsWide != 0)
                rows++;

            int x = pt.X;
            int y = pt.Y;
            PurchasePoint = pt;

            if (shop.upperRightCloseButton.containsPoint(x, y))
            {
                shop.exitThisMenu();
                return true;
            }
            if (downArrow.containsPoint(x, y))
            {
                DoScroll(-1);
                return true;
            }
            if (upArrow.containsPoint(x, y))
            {
                DoScroll(1);
                return true;
            }
            if (scrollBarRunner.Contains(x, y))
            {
                int y1 = scrollBar.bounds.Y;
                scrollBar.bounds.Y = Math.Min(shop.yPositionOnScreen + shop.height - 64 - 12 - scrollBar.bounds.Height, Math.Max(y, shop.yPositionOnScreen + upArrow.bounds.Height + 20));
                currentItemIndex = (int)Math.Round((double)Math.Max(1, rows - Mod.UnitsHigh) * ((y - scrollBarRunner.Y) / (float)scrollBarRunner.Height));
                shop.currentItemIndex = currentItemIndex;
                if (forSale.Count > 0)
                {
                    scrollBar.bounds.Y = scrollBarRunner.Height / Math.Max(1, rows - Mod.UnitsHigh) * currentItemIndex + upArrow.bounds.Bottom + 4;
                    if (currentItemIndex >= rows - Mod.UnitsHigh)
                        scrollBar.bounds.Y = downArrow.bounds.Y - scrollBar.bounds.Height - 4;
                }
                int y2 = scrollBar.bounds.Y;
                if (y1 == y2)
                    return true;
                Game1.playSound("shiny4");
                return true;
            }
            for (int i = 0; i < shop.tabButtons.Count; i++)
                if (shop.tabButtons[i].containsPoint(x, y))
                {
                    // switchTab changes the forSale list based on the tab.
                    shop.switchTab(i);
                    InitialItems = Shop.forSale;
                    InitialStock = Shop.itemPriceAndStock;

                    // the tabs filter, but we do have our filter and some items/filters may overlap (dressers), so redo our filter.
                    SyncStock();
                    return true;
                }

            // say any click within the grid layout area is handled.
            var menuRect = new Rectangle(shop.xPositionOnScreen, shop.yPositionOnScreen, shop.width, shop.height - 256 + 32 + 4);
            bool clickHandled = menuRect.Contains(x, y);

            var clickableComponent = shop.inventory.snapToClickableComponent(x, y);
            if (shop.heldItem == null)
            {
                var item = shop.inventory.leftClick(x, y, null, false);
                if (item != null)
                {
                    clickHandled = true; // was null, now not. picked/selected an item in inventory to sell.

                    if (shop.onSell != null)
                        shop.onSell(item);
                    else
                    {
                        ShopMenu.chargePlayer(Game1.player, shop.currency, -((item is SObject obj ? (int)(obj.sellToStorePrice() * (double)sellPercentage) : (int)(item.salePrice() / 2d * sellPercentage)) * item.Stack));
                        int num = item.Stack / 8 + 2;
                        for (int index = 0; index < num; ++index)
                        {
                            animations.Add(new TemporaryAnimatedSprite("TileSheets\\debris", new Rectangle(Game1.random.Next(2) * 16, 64, 16, 16), 9999f, 1, 999, clickableComponent + new Vector2(32f, 32f), false, false)
                            {
                                alphaFade = 0.025f, motion = new Vector2(Game1.random.Next(-3, 4), -4f), acceleration = new Vector2(0.0f, 0.5f), delayBeforeAnimationStart = index * 25,
                                scale = 2f
                            });
                            animations.Add(new TemporaryAnimatedSprite("TileSheets\\debris", new Rectangle(Game1.random.Next(2) * 16, 64, 16, 16), 9999f, 1, 999, clickableComponent + new Vector2(32f, 32f), false, false)
                            {
                                scale = 4f, alphaFade = 0.025f, delayBeforeAnimationStart = index * 50, motion = Utility.getVelocityTowardPoint(new Point((int)clickableComponent.X + 32, (int)clickableComponent.Y + 32), new Vector2(shop.xPositionOnScreen - 36, shop.yPositionOnScreen + shop.height - shop.inventory.height - 16), 8f),
                                acceleration = Utility.getVelocityTowardPoint(new Point((int)clickableComponent.X + 32, (int)clickableComponent.Y + 32), new Vector2(shop.xPositionOnScreen - 36, shop.yPositionOnScreen + shop.height - shop.inventory.height - 16), 0.5f)
                            });
                        }
                        if (item is SObject o && o.Edibility != -300)
                        {
                            var one = item.getOne();
                            one.Stack = item.Stack;
                            (Game1.getLocationFromName("SeedShop") as SeedShop)?.itemsToStartSellingTomorrow.Add(one);
                        }
                        Game1.playSound("sell");
                        Game1.playSound("purchase");
                    }
                }
            }
            else
            {
                shop.heldItem = shop.inventory.leftClick(x, y, (Item)shop.heldItem);
                clickHandled = clickHandled || shop.heldItem == null; //placed heldItem into inventory.
            }

            for (int i = currentItemIndex * Mod.UnitsWide; i < forSale.Count && i < currentItemIndex * Mod.UnitsWide + Mod.UnitsWide * 3; ++i)
            {
                int ix = i % Mod.UnitsWide;
                int iy = i / Mod.UnitsWide;
                var rect = new Rectangle(shop.xPositionOnScreen + 16 + ix * Mod.UnitWidth,
                    shop.yPositionOnScreen + 16 + iy * Mod.UnitHeight - currentItemIndex * Mod.UnitHeight,
                    Mod.UnitWidth, Mod.UnitHeight);
                if (!rect.Contains(x, y) || forSale[i] == null)
                    continue;
                int numberToBuy = !e.IsDown(SButton.LeftShift) ? 1 : e.IsDown(SButton.LeftControl) ? 25 : 5;

                PurchaseItem(numberToBuy, i);
                break;
            }

            return clickHandled;
        }

        private void DoGridLayoutRightClick(ButtonPressedEventArgs e, Point pt)
        {
            var shop = Shop;
            var forSale = shop.forSale;
            var itemPriceAndStock = shop.itemPriceAndStock;
            var animations = ReflectAnimations.GetValue();
            int currentItemIndex = shop.currentItemIndex;
            float sellPercentage = ReflectSellPercentage.GetValue();
            int delayTime = Mod.PurchaseCountdownStart;

            int x = pt.X;
            int y = pt.Y;
            PurchasePoint = pt;

            // Copying a lot from right click code
            var clickableComponent = shop.inventory.snapToClickableComponent(x, y);
            if (shop.heldItem == null)
            {
                var item = shop.inventory.rightClick(x, y, null, false);
                if (item != null)
                {
                    if (shop.onSell != null)
                        shop.onSell(item);
                    else
                    {
                        ShopMenu.chargePlayer(Game1.player, shop.currency, -((item is SObject obj ? (int)(obj.sellToStorePrice() * (double)sellPercentage) : (int)(item.salePrice() / 2d * sellPercentage)) * item.Stack));
                        Game1.playSound(Game1.mouseClickPolling > 300 ? "purchaseRepeat" : "purchaseClick");
                        const int coins = 2;
                        for (int j = 0; j < coins; j++)
                        {
                            animations.Add(new TemporaryAnimatedSprite("TileSheets\\debris", new Rectangle(Game1.random.Next(2) * 16, 64, 16, 16), 9999f, 1, 999, clickableComponent + new Vector2(32f, 32f), false, false)
                            {
                                alphaFade = 0.025f, motion = new Vector2(Game1.random.Next(-3, 4), -4f), acceleration = new Vector2(0f, 0.5f), delayBeforeAnimationStart = j * 25,
                                scale = 2f
                            });
                            var moneyBox = new Vector2(shop.xPositionOnScreen - 36, shop.yPositionOnScreen + shop.height - shop.inventory.height - 16);
                            animations.Add(new TemporaryAnimatedSprite("TileSheets\\debris", new Rectangle(Game1.random.Next(2) * 16, 64, 16, 16), 9999f, 1, 999, clickableComponent + new Vector2(32f, 32f), false, false)
                            {
                                scale = 4f, alphaFade = 0.025f, delayBeforeAnimationStart = j * 50, motion = Utility.getVelocityTowardPoint(new Point((int)clickableComponent.X + 32, (int)clickableComponent.Y + 32), moneyBox, 12f),
                                acceleration = Utility.getVelocityTowardPoint(new Point((int)clickableComponent.X + 32, (int)clickableComponent.Y + 32), moneyBox, 0.5f)
                            });
                        }

                        if (item is SObject o && o.Edibility != -300)
                            (Game1.getLocationFromName("SeedShop") as SeedShop)?.itemsToStartSellingTomorrow.Add(item.getOne());
                        if (shop.inventory.getItemAt(x, y) == null)
                        {
                            Game1.playSound("sell");
                            animations.Add(new TemporaryAnimatedSprite(5, clickableComponent + new Vector2(32f, 32f), Color.White)
                            {
                                motion = new Vector2(0.0f, -0.5f)
                            });
                        }
                    }
                }
            }
            else
            {
                if (PurchaseCountdown == 0)
                    delayTime = Mod.PurchaseCountdownRepeat;
                shop.heldItem = shop.inventory.rightClick(x, y, shop.heldItem as Item);
            }

            for (int i = currentItemIndex * Mod.UnitsWide; i < forSale.Count && i < currentItemIndex * Mod.UnitsWide + Mod.UnitsWide * 3; ++i)
            {
                int ix = i % Mod.UnitsWide;
                int iy = i / Mod.UnitsWide;
                var rect = new Rectangle(shop.xPositionOnScreen + 16 + ix * Mod.UnitWidth,
                    shop.yPositionOnScreen + 16 + iy * Mod.UnitHeight - currentItemIndex * Mod.UnitHeight,
                    Mod.UnitWidth, Mod.UnitHeight);
                if (!rect.Contains(x, y) || forSale[i] == null)
                    continue;
                bool leftShiftDown = e?.IsDown(SButton.LeftShift) ?? Helper.Input.IsDown(SButton.LeftShift);
                bool leftCtrlDown = e?.IsDown(SButton.LeftControl) ?? Helper.Input.IsDown(SButton.LeftControl);
                int numberToBuy = !leftShiftDown ? 1 : leftCtrlDown ? 25 : 5;
                numberToBuy = Math.Min(
                    Math.Min(numberToBuy, ShopMenu.getPlayerCurrencyAmount(Game1.player, shop.currency) / Math.Max(1, itemPriceAndStock[forSale[i]].Price)),
                    Math.Max(1, itemPriceAndStock[forSale[i]].Stock)
                    );
                numberToBuy = Math.Min(numberToBuy, forSale[i].maximumStackSize());

                //tryToPurchase may change heldItem.
                if (numberToBuy > 0 && ReflectTryToPurchaseItem.Invoke<bool>(forSale[i], shop.heldItem, numberToBuy, x, y, i))
                {
                    itemPriceAndStock.Remove(forSale[i]);
                    forSale.RemoveAt(i);
                }

                if (
                    shop.heldItem != null &&
                    (ReflectIsStorageShop.GetValue() || Game1.options.SnappyMenus) &&
                    Game1.activeClickableMenu is ShopMenu &&
                    Game1.player.addItemToInventoryBool(shop.heldItem as Item)
                    )
                {
                    shop.heldItem = null;
                    DelayedAction.playSoundAfterDelay("coin", 100);
                }
                else
                    PurchaseCountdown = delayTime;
                break;
            }
        }
    }

    internal class ShopMenuPatches
    {
        public static bool ShopMenu_receiveScrollWheelAction_Prefix(int direction)
        {
            try
            {
                return !Mod.Instance.GridLayoutActive;

                // don't run original logic
            }
            catch (Exception ex)
            {
                Mod.Instance.Monitor.Log($"Failed in {nameof(ShopMenuPatches.ShopMenu_receiveScrollWheelAction_Prefix)}:\n{ex}", LogLevel.Error);
                return true; // run original logic
            }
        }

        public static bool ShopMenu_performHoverAction_Prefix(int x, int y)
        {
            try
            {
                if (Mod.Instance.GridLayoutActive)
                {
                    var shop = Mod.Instance.Shop;
                    if (shop != null)
                    {
                        // just do these
                        shop.upperRightCloseButton.tryHover(x, y, 0.5f);
                        //shop.upArrow.tryHover(x, y);
                        //shop.downArrow.tryHover(x, y);
                        //shop.scrollBar.tryHover(x, y);

                        // if in the grid layout area, then patch hover out. otherwise allow. e.g. inventory menu
                        var menuRect = new Rectangle(shop.xPositionOnScreen, shop.yPositionOnScreen, shop.width, shop.height - 256 + 32 + 4);
                        if (menuRect.Contains(x, y))
                            return false; // don't run original logic
                    }
                }

                Mod.Instance.ActiveButton.tryHover(x, y, 0.4f);

                return true;
            }
            catch (Exception ex)
            {
                Mod.Instance.Monitor.Log($"Failed in {nameof(ShopMenuPatches.ShopMenu_performHoverAction_Prefix)}:\n{ex}", LogLevel.Error);
                return true; // run original logic
            }
        }

        public static bool ShopMenu_receiveRightClick_Prefix(int x, int y, bool playSound = true)
        {
            try
            {
                return !Mod.Instance.GridLayoutActive;
                // don't run original logic
            }
            catch (Exception ex)
            {
                Mod.Instance.Monitor.Log($"Failed in {nameof(ShopMenuPatches.ShopMenu_receiveRightClick_Prefix)}:\n{ex}", LogLevel.Error);
                return true; // run original logic
            }
        }

        public static bool ShopMenu_draw_Prefix(SpriteBatch b)
        {
            try
            {
                return !Mod.Instance.GridLayoutActive;
                // don't run original logic
            }
            catch (Exception ex)
            {
                Mod.Instance.Monitor.Log($"Failed in {nameof(ShopMenuPatches.ShopMenu_draw_Prefix)}:\n{ex}", LogLevel.Error);
                return true; // run original logic
            }
        }
    }
}
