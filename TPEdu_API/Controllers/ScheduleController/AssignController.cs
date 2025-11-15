using BusinessLayer.DTOs.API;
using BusinessLayer.DTOs.Schedule.Class;
using BusinessLayer.DTOs.Schedule.ClassAssign; // AssignRecurringClassDto
using BusinessLayer.Service.Interface; // IStudentProfileService
using BusinessLayer.Service.Interface.IScheduleService;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading.Tasks;
using TPEdu_API.Common.Extensions; // RequireUserId()

namespace TPEdu_API.Controllers.ScheduleController
{
    [Route("tpedu/v1/assigns")]
    [ApiController]
    [Authorize(Roles = "Student,Parent")] // Only allow Students and Parents
    public class AssignController : ControllerBase
    {
        private readonly IAssignService _assignService;
        private readonly IStudentProfileService _studentProfileService;

        // Constructor
        public AssignController(IAssignService assignService, IStudentProfileService studentProfileService)
        {
            _assignService = assignService;
            _studentProfileService = studentProfileService;
        }
        /// <summary>
        /// [Student] Assign the student to a class.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> AssignStudentToClass([FromBody] AssignRecurringClassDto assignDto)
        {
            // Take userId from JWT token
            var userId = User.RequireUserId();
            var updatedClass = await _assignService.AssignRecurringClassAsync(userId, assignDto.ClassId);

            // Return response
            return Ok(ApiResponse<ClassDto>.Ok(updatedClass));
        }

        /// <summary>
        /// [Student] Withdraw from a class.
        /// </summary>
        [HttpDelete("{classId}")]
        public async Task<IActionResult> WithdrawFromClass(string classId)
        {
            var userId = User.RequireUserId();

            await _assignService.WithdrawFromClassAsync(userId, classId);

            return Ok(ApiResponse<object>.Ok(null, "Rút khỏi lớp thành công."));
        }
    }
}