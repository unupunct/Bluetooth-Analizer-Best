using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BluetoothFleetMonitor.Database.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApplicationEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConnectionTests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScannerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    SuccessfulAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    FailedAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeoutCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorDetails = table.Column<string>(type: "TEXT", nullable: true),
                    TestConfiguration = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectionTests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomName = table.Column<string>(type: "TEXT", nullable: true),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Manufacturer = table.Column<string>(type: "TEXT", nullable: true),
                    AdvertisedName = table.Column<string>(type: "TEXT", nullable: true),
                    IdentityFingerprint = table.Column<string>(type: "TEXT", nullable: true),
                    AssignedRoom = table.Column<string>(type: "TEXT", nullable: true),
                    AssignedWorkstation = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRssi = table.Column<short>(type: "INTEGER", nullable: true),
                    MinRssi = table.Column<short>(type: "INTEGER", nullable: true),
                    MaxRssi = table.Column<short>(type: "INTEGER", nullable: true),
                    RssiSum = table.Column<long>(type: "INTEGER", nullable: false),
                    ObservationCount = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FaultReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FaultType = table.Column<string>(type: "TEXT", nullable: false),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    HealthScore = table.Column<double>(type: "REAL", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FaultReports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InspectionSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScannerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LocationLabel = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InspectionSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Observations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScannerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UploadedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ProcessedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Rssi = table.Column<short>(type: "INTEGER", nullable: false),
                    Address = table.Column<string>(type: "TEXT", nullable: false),
                    AddressKind = table.Column<int>(type: "INTEGER", nullable: false),
                    ManufacturerData = table.Column<string>(type: "TEXT", nullable: true),
                    ServiceUuids = table.Column<string>(type: "TEXT", nullable: true),
                    AdvertisementData = table.Column<string>(type: "TEXT", nullable: true),
                    InspectionSessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LocationLabel = table.Column<string>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Observations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PresenceEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScannerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EventType = table.Column<string>(type: "TEXT", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Duration = table.Column<TimeSpan>(type: "TEXT", nullable: true),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    Explanation = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PresenceEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Scanners",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Platform = table.Column<string>(type: "TEXT", nullable: false),
                    HardwareDescription = table.Column<string>(type: "TEXT", nullable: true),
                    LastSeen = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Scanners", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UsbEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    HardwareId = table.Column<string>(type: "TEXT", nullable: true),
                    VendorId = table.Column<string>(type: "TEXT", nullable: true),
                    ProductId = table.Column<string>(type: "TEXT", nullable: true),
                    DeviceInstanceId = table.Column<string>(type: "TEXT", nullable: true),
                    EventType = table.Column<string>(type: "TEXT", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsbEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceIdentifiers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IdentifierType = table.Column<string>(type: "TEXT", nullable: false),
                    IdentifierValue = table.Column<string>(type: "TEXT", nullable: false),
                    FirstSeen = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeen = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    IsUserConfirmed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceIdentifiers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceIdentifiers_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationEvents_EventType",
                table: "ApplicationEvents",
                column: "EventType");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationEvents_TimestampUtc",
                table: "ApplicationEvents",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectionTests_DeviceId",
                table: "ConnectionTests",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectionTests_StartedAtUtc",
                table: "ConnectionTests",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceIdentifiers_DeviceId",
                table: "DeviceIdentifiers",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceIdentifiers_IdentifierType_IdentifierValue",
                table: "DeviceIdentifiers",
                columns: new[] { "IdentifierType", "IdentifierValue" });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_AssignedRoom",
                table: "Devices",
                column: "AssignedRoom");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_Category",
                table: "Devices",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_IdentityFingerprint",
                table: "Devices",
                column: "IdentityFingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_LastSeenUtc",
                table: "Devices",
                column: "LastSeenUtc");

            migrationBuilder.CreateIndex(
                name: "IX_FaultReports_CreatedAtUtc",
                table: "FaultReports",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_FaultReports_DeviceId",
                table: "FaultReports",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_InspectionSessions_LocationLabel",
                table: "InspectionSessions",
                column: "LocationLabel");

            migrationBuilder.CreateIndex(
                name: "IX_InspectionSessions_ScannerId",
                table: "InspectionSessions",
                column: "ScannerId");

            migrationBuilder.CreateIndex(
                name: "IX_Observations_DeviceId",
                table: "Observations",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_Observations_DeviceId_TimestampUtc",
                table: "Observations",
                columns: new[] { "DeviceId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Observations_LocationLabel",
                table: "Observations",
                column: "LocationLabel");

            migrationBuilder.CreateIndex(
                name: "IX_Observations_ScannerId",
                table: "Observations",
                column: "ScannerId");

            migrationBuilder.CreateIndex(
                name: "IX_Observations_TimestampUtc",
                table: "Observations",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PresenceEvents_DeviceId",
                table: "PresenceEvents",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_PresenceEvents_EventType",
                table: "PresenceEvents",
                column: "EventType");

            migrationBuilder.CreateIndex(
                name: "IX_PresenceEvents_ScannerId",
                table: "PresenceEvents",
                column: "ScannerId");

            migrationBuilder.CreateIndex(
                name: "IX_PresenceEvents_TimestampUtc",
                table: "PresenceEvents",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_UsbEvents_EventType",
                table: "UsbEvents",
                column: "EventType");

            migrationBuilder.CreateIndex(
                name: "IX_UsbEvents_TimestampUtc",
                table: "UsbEvents",
                column: "TimestampUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApplicationEvents");

            migrationBuilder.DropTable(
                name: "ConnectionTests");

            migrationBuilder.DropTable(
                name: "DeviceIdentifiers");

            migrationBuilder.DropTable(
                name: "FaultReports");

            migrationBuilder.DropTable(
                name: "InspectionSessions");

            migrationBuilder.DropTable(
                name: "Observations");

            migrationBuilder.DropTable(
                name: "PresenceEvents");

            migrationBuilder.DropTable(
                name: "Scanners");

            migrationBuilder.DropTable(
                name: "UsbEvents");

            migrationBuilder.DropTable(
                name: "Devices");
        }
    }
}
