using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoTrader.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Batch13_PersistenceIndexesAndConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderExecutions");

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Positions",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Orders",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "ProfitFactor",
                table: "BacktestRuns",
                type: "decimal(18,2)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,2)");

            migrationBuilder.AddColumn<string>(
                name: "CostModel",
                table: "BacktestRuns",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DataFingerprint",
                table: "BacktestRuns",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "FinalCapital",
                table: "BacktestRuns",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "ParametersHash",
                table: "BacktestRuns",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RunCorrelationId",
                table: "BacktestRuns",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SlippageModel",
                table: "BacktestRuns",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Universe",
                table: "BacktestRuns",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Positions_Status",
                table: "Positions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CreatedAtUtc",
                table: "Orders",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_State",
                table: "Orders",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_Instruments_Exchange_Segment_IsTradable",
                table: "Instruments",
                columns: new[] { "Exchange", "Segment", "IsTradable" });

            migrationBuilder.CreateIndex(
                name: "IX_Instruments_Exchange_Symbol",
                table: "Instruments",
                columns: new[] { "Exchange", "Symbol" });

            migrationBuilder.CreateIndex(
                name: "IX_Instruments_InstrumentToken",
                table: "Instruments",
                column: "InstrumentToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BacktestRuns_RunCorrelationId",
                table: "BacktestRuns",
                column: "RunCorrelationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Positions_Status",
                table: "Positions");

            migrationBuilder.DropIndex(
                name: "IX_Orders_CreatedAtUtc",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_State",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Instruments_Exchange_Segment_IsTradable",
                table: "Instruments");

            migrationBuilder.DropIndex(
                name: "IX_Instruments_Exchange_Symbol",
                table: "Instruments");

            migrationBuilder.DropIndex(
                name: "IX_Instruments_InstrumentToken",
                table: "Instruments");

            migrationBuilder.DropIndex(
                name: "IX_BacktestRuns_RunCorrelationId",
                table: "BacktestRuns");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Positions");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CostModel",
                table: "BacktestRuns");

            migrationBuilder.DropColumn(
                name: "DataFingerprint",
                table: "BacktestRuns");

            migrationBuilder.DropColumn(
                name: "FinalCapital",
                table: "BacktestRuns");

            migrationBuilder.DropColumn(
                name: "ParametersHash",
                table: "BacktestRuns");

            migrationBuilder.DropColumn(
                name: "RunCorrelationId",
                table: "BacktestRuns");

            migrationBuilder.DropColumn(
                name: "SlippageModel",
                table: "BacktestRuns");

            migrationBuilder.DropColumn(
                name: "Universe",
                table: "BacktestRuns");

            migrationBuilder.AlterColumn<decimal>(
                name: "ProfitFactor",
                table: "BacktestRuns",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,2)",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "OrderExecutions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderId = table.Column<long>(type: "bigint", nullable: false),
                    BrokerExecutionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ExecutionTimestampUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FillPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    FilledQuantity = table.Column<int>(type: "int", nullable: false),
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

            migrationBuilder.CreateIndex(
                name: "IX_OrderExecutions_OrderId",
                table: "OrderExecutions",
                column: "OrderId");
        }
    }
}
