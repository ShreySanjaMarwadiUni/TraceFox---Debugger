using Microsoft.AspNetCore.Mvc;
using MySql.Data.MySqlClient;
using System.Data;

namespace TraceFox___Debugger.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class CodeController : ControllerBase
    {
        private readonly string _connStr = "Server=localhost;Database=tracefoxdb;Uid=root;Pwd=12345;";

        public class CodeRequest
        {
            public string Code { get; set; }
        }

        // ✅ Load code from DB
        [HttpGet("load")]
        public async Task<IActionResult> LoadCode()
        {
            try
            {
                using var conn = new MySqlConnection(_connStr);
                await conn.OpenAsync();

                var cmd = new MySqlCommand("SELECT code FROM code_storage WHERE id = 1 LIMIT 1;", conn);
                var result = await cmd.ExecuteScalarAsync();

                return Ok(new { code = result?.ToString() ?? "" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Failed to load code", details = ex.Message });
            }
        }

        // ✅ Save or update code safely
        [HttpPost("save")]
        public async Task<IActionResult> SaveCode([FromBody] CodeRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.Code))
                return BadRequest(new { error = "Empty code." });

            try
            {
                using var conn = new MySqlConnection(_connStr);
                await conn.OpenAsync();

                // ✅ If record doesn't exist, insert it; otherwise, update
                var cmd = new MySqlCommand(@"
                    INSERT INTO code_storage (id, code)
                    VALUES (1, @code)
                    ON DUPLICATE KEY UPDATE code = VALUES(code);
                ", conn);
                cmd.Parameters.AddWithValue("@code", req.Code);
                await cmd.ExecuteNonQueryAsync();

                Console.WriteLine($"[CodeController] Code saved successfully (length: {req.Code.Length})");

                return Ok(new
                {
                    success = true,
                    message = "Code saved successfully.",
                    savedLength = req.Code.Length
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine("[CodeController] Error saving code: " + ex.Message);
                return StatusCode(500, new { error = "Failed to save code", details = ex.Message });
            }
        }
    }
}
