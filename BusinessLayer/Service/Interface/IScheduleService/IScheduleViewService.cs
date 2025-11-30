using BusinessLayer.DTOs.Schedule.ScheduleEntry;
using DataLayer.Enum;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BusinessLayer.Service.Interface.IScheduleService
{
    public interface IScheduleViewService
    {
        // Get a tutor's schedule entries between startDate and endDate
        Task<IEnumerable<ScheduleEntryDto>> GetTutorScheduleAsync(
            string tutorId, 
            DateTime startDate, 
            DateTime endDate, 
            string? entryType, 
            string? classId = null,
            string? filterByStudentId = null);

        // Get a student's schedule entries between startDate and endDate
        Task<IEnumerable<ScheduleEntryDto>> GetStudentScheduleAsync(
            string studentUserId, 
            DateTime startDate, 
            DateTime endDate,
            string? filterByTutorId = null);

        // View schedule of a specific child
        Task<IEnumerable<ScheduleEntryDto>> GetChildScheduleAsync(
            string parentUserId,
            string childId, // StudentProfileId
            DateTime startDate,
            DateTime endDate);

        // View combined schedule of all children of a parent
        Task<IEnumerable<ScheduleEntryDto>> GetAllChildrenScheduleAsync(
            string parentUserId,
            DateTime startDate,
            DateTime endDate);
    }
}
