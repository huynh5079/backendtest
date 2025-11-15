using BusinessLayer.DTOs.Schedule.Class;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BusinessLayer.Service.Interface.IScheduleService
{
    public interface IAssignService
    {
        // Student assign to a recurring class
        Task<ClassDto> AssignRecurringClassAsync(string studentUserId, string classId);

        Task<bool> WithdrawFromClassAsync(string studentUserId, string classId);
    }
}
