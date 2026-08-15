using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoTrader.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Instruments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InstrumentToken = table.Column<int>(type: "int", nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Exchange = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Segment = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TickSize = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    LotSize = table.Column<int>(type: "int", nullable: false),
                    IsTradable = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Instruments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LiveTrades",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InstrumentToken = table.Column<int>(type: "int", nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    StrategyName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    StrategyVersion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EntryTimestampUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExitTimestampUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Side = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    EntryPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ExitPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    GrossPnl = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Charges = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    NetPnl = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveTrades", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MarketCandles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InstrumentToken = table.Column<int>(type: "int", nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Exchange = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Open = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    High = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Low = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Close = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Volume = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketCandles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MarketTicks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InstrumentToken = table.Column<int>(type: "int", nullable: false),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    BidPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    AskPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Volume = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketTicks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BrokerOrderId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    InstrumentToken = table.Column<int>(type: "int", nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Exchange = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Side = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Validity = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Product = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    Price = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    TriggerPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    FilledQuantity = table.Column<int>(type: "int", nullable: false),
                    AverageFillPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    State = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RejectionReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Tag = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    StrategyName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastUpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FilledAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaperTrades",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InstrumentToken = table.Column<int>(type: "int", nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    StrategyName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    StrategyVersion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EntryTimestampUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExitTimestampUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Side = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    EntryPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ExitPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    GrossPnl = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Charges = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    NetPnl = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperTrades", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PortfolioSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SnapshotTimestampUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TotalCapital = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    AvailableCash = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    InvestedCapital = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    UnrealizedPnl = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    RealizedPnl = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    OpenPositionCount = table.Column<int>(type: "int", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortfolioSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Positions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InstrumentToken = table.Column<int>(type: "int", nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    StrategyName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    AveragePrice = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    OpenedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ClosedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    StopPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    TargetPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    RealizedPnl = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Positions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RiskEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventTimestampUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Details = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    StrategyName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiskEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Strategies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Strategies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventTimestampUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Details = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TradingDays",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    IsTradingDay = table.Column<bool>(type: "bit", nullable: false),
                    SessionStartUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SessionEndUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradingDays", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrderExecutions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderId = table.Column<long>(type: "bigint", nullable: false),
                    ExecutionTimestampUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FilledQuantity = table.Column<int>(type: "int", nullable: false),
                    FillPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    BrokerExecutionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderExecutions_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StrategyVersions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StrategyId = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StrategyVersions_Strategies_StrategyId",
                        column: x => x.StrategyId,
                        principalTable: "Strategies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BacktestRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StrategyVersionId = table.Column<long>(type: "bigint", nullable: false),
                    DataStartUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DataEndUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    InitialCapital = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ExecutionModel = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TotalTrades = table.Column<int>(type: "int", nullable: false),
                    WinningTrades = table.Column<int>(type: "int", nullable: false),
                    LosingTrades = table.Column<int>(type: "int", nullable: false),
                    WinRate = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    GrossPnl = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TotalCharges = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TotalSlippage = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    NetPnl = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    MaxDrawdown = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ProfitFactor = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    SharpeRatio = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    SortinoRatio = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BacktestRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BacktestRuns_StrategyVersions_StrategyVersionId",
                        column: x => x.StrategyVersionId,
                        principalTable: "StrategyVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BacktestTrades",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BacktestRunId = table.Column<long>(type: "bigint", nullable: false),
                    InstrumentToken = table.Column<int>(type: "int", nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    EntryTimestampUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExitTimestampUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Side = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    EntryPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ExitPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    GrossPnl = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Charges = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Slippage = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    NetPnl = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    HoldingMinutes = table.Column<int>(type: "int", nullable: false),
                    ExitReason = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BacktestTrades", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BacktestTrades_BacktestRuns_BacktestRunId",
                        column: x => x.BacktestRunId,
                        principalTable: "BacktestRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRuns_StrategyVersionId",
                table: "BacktestRuns",
                column: "StrategyVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_BacktestTrades_BacktestRunId",
                table: "BacktestTrades",
                column: "BacktestRunId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveTrades_CorrelationId",
                table: "LiveTrades",
                column: "CorrelationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketCandles_InstrumentToken_Timeframe_TimestampUtc",
                table: "MarketCandles",
                columns: new[] { "InstrumentToken", "Timeframe", "TimestampUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MarketTicks_InstrumentToken_TimestampUtc",
                table: "MarketTicks",
                columns: new[] { "InstrumentToken", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderExecutions_OrderId",
                table: "OrderExecutions",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_BrokerOrderId",
                table: "Orders",
                column: "BrokerOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CorrelationId",
                table: "Orders",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_PaperTrades_CorrelationId",
                table: "PaperTrades",
                column: "CorrelationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioSnapshots_SnapshotTimestampUtc",
                table: "PortfolioSnapshots",
                column: "SnapshotTimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Positions_CorrelationId",
                table: "Positions",
                column: "CorrelationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RiskEvents_EventTimestampUtc",
                table: "RiskEvents",
                column: "EventTimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Strategies_Name",
                table: "Strategies",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StrategyVersions_StrategyId_Version",
                table: "StrategyVersions",
                columns: new[] { "StrategyId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SystemEvents_EventTimestampUtc",
                table: "SystemEvents",
                column: "EventTimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TradingDays_Date",
                table: "TradingDays",
                column: "Date",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BacktestTrades");

            migrationBuilder.DropTable(
                name: "Instruments");

            migrationBuilder.DropTable(
                name: "LiveTrades");

            migrationBuilder.DropTable(
                name: "MarketCandles");

            migrationBuilder.DropTable(
                name: "MarketTicks");

            migrationBuilder.DropTable(
                name: "OrderExecutions");

            migrationBuilder.DropTable(
                name: "PaperTrades");

            migrationBuilder.DropTable(
                name: "PortfolioSnapshots");

            migrationBuilder.DropTable(
                name: "Positions");

            migrationBuilder.DropTable(
                name: "RiskEvents");

            migrationBuilder.DropTable(
                name: "SystemEvents");

            migrationBuilder.DropTable(
                name: "TradingDays");

            migrationBuilder.DropTable(
                name: "BacktestRuns");

            migrationBuilder.DropTable(
                name: "Orders");

            migrationBuilder.DropTable(
                name: "StrategyVersions");

            migrationBuilder.DropTable(
                name: "Strategies");
        }
    }
}
